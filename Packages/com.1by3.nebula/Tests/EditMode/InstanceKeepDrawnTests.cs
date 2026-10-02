using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// A client can keep another scope's content drawn alongside the local pawn's (<see cref="InstanceScenes.KeepDrawn"/>):
    /// the static content <see cref="InstanceScenes.Prepare"/> loads for an instance part, and a scoped grid's chunk
    /// content. Requests are counted, content created while a scope is kept starts drawn, withdrawing restores exactly
    /// what Nebula turned off, and a release cleans up. With the API unused, the view behaves as before.
    /// EditMode cannot create a runtime scene, so static content is handed over with
    /// <see cref="InstanceScenes.AdoptContent"/>, the step <see cref="InstanceScenes.Prepare"/> ends with.
    /// </summary>
    public sealed class InstanceKeepDrawnTests
    {
        private const string HostKey = "world/host";
        private static readonly Vector3 Cell = new Vector3(256f, 256f, 256f);
        private static readonly ulong VaultA = NebulaWorker.InstanceKey("vault/a");
        private static readonly ulong VaultB = NebulaWorker.InstanceKey("vault/b");

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
            NebulaRuntime.IsClient = true;
            NebulaChunks.Activate(_host, NebulaRoles.Client, false, null);
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

        // ------------------------------------------------------------------------------------ the world

        /// <summary>A chunk of the host grid with one renderer under its content root.</summary>
        private Container HostChunk(Vector3Int coord, out MeshRenderer rock)
        {
            var c = ContainerRegistry.RegisterRuntime(_host.IdOf(coord), _host.BoundsOf(coord), new InstanceContainerInfo
            {
                InstanceId = _host.InstanceId, ScopeKey = _host.ScopeKey, PartId = ChunkKeys.PartId(coord),
            });
            ContainerRegistry.ApplyLease(c.ContainerId, "w-1", 1, 1);
            rock = Renderer("rock", NebulaChunks.RootOf(c.RuntimeId));
            return c;
        }

        /// <summary>A part of an instance whose prepared static content holds one renderer.</summary>
        private static Container Instance(string key, Vector3 center, out MeshRenderer wall, string part = "interior")
        {
            string scopeKey = "vault/" + key;
            var c = ContainerRegistry.RegisterRuntime(NebulaWorker.InstanceKey(scopeKey + "/" + part), new Bounds(center, new Vector3(40, 20, 40)), new InstanceContainerInfo
            {
                InstanceId = NebulaWorker.InstanceKey(scopeKey), ScopeKey = scopeKey, PartId = part,
            });
            ContainerRegistry.ApplyLease(c.ContainerId, "w-1", 1, 1);
            var content = new GameObject("Content");
            content.transform.SetParent(c.transform, false);
            wall = Renderer("wall", content.transform);
            InstanceScenes.AdoptContent(c, content);
            return c;
        }

        private static MeshRenderer Renderer(string name, Transform parent)
        {
            var renderer = new GameObject(name, typeof(MeshRenderer)).GetComponent<MeshRenderer>();
            renderer.transform.SetParent(parent, false);
            return renderer;
        }

        private ChunkContext Context(Container chunk) =>
            new ChunkContext(Vector3Int.zero, chunk.RuntimeId, chunk, NebulaChunks.RootOf(chunk.RuntimeId), NebulaRoles.Client, false, _host);

        private static int HiddenCount(Type owner, string field) =>
            ((IDictionary)owner.GetField(field, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).Count;

        // ------------------------------------------------------------------------------------ tests

        [Test]
        public void KeepingAScopeDrawnShowsItsChunksAndClearingHidesThemAgain()
        {
            var chunk = HostChunk(Vector3Int.zero, out var rock);
            var decal = Renderer("decal", NebulaChunks.RootOf(chunk.RuntimeId));
            decal.forceRenderingOff = true;
            InstanceScenes.SetView(VaultA);
            Assert.IsTrue(rock.forceRenderingOff, "the host is hidden from inside the instance");
            Assert.IsFalse(InstanceScenes.IsDrawn(_host.InstanceId));

            InstanceScenes.KeepDrawn(_host.InstanceId);
            Assert.IsFalse(rock.forceRenderingOff, "kept drawn, the host shows through the doorway");
            Assert.IsTrue(decal.forceRenderingOff, "a renderer the game turned off stays off");
            Assert.IsTrue(NebulaChunks.IsDrawn(Context(chunk)));
            Assert.IsTrue(InstanceScenes.IsDrawn(_host.InstanceId));

            Assert.IsTrue(InstanceScenes.StopKeepingDrawn(_host.InstanceId));
            Assert.IsTrue(rock.forceRenderingOff, "withdrawn, the host is hidden again");
            Assert.IsFalse(NebulaChunks.IsDrawn(Context(chunk)));
            Assert.IsFalse(InstanceScenes.IsDrawn(_host.InstanceId));

            InstanceScenes.SetView(_host.InstanceId);
            Assert.IsFalse(rock.forceRenderingOff);
            Assert.IsTrue(decal.forceRenderingOff, "and still off once the pawn is back");
        }

        [Test]
        public void KeepingAScopeDrawnShowsItsStaticContentAndClearingHidesItAgain()
        {
            InstanceScenes.SetView(_host.InstanceId);
            Instance("b", new Vector3(10, 0, 10), out var wall);
            var sign = Renderer("sign", wall.transform.parent);
            InstanceScenes.SetView(_host.InstanceId);
            Assert.IsTrue(wall.forceRenderingOff, "another instance's content is resident but hidden");
            Assert.IsTrue(sign.forceRenderingOff, "content added since is hidden at the next view change");

            InstanceScenes.KeepDrawn(VaultB);
            Assert.IsFalse(wall.forceRenderingOff, "kept drawn, the instance shows through its open door");
            Assert.IsFalse(sign.forceRenderingOff);

            InstanceScenes.StopKeepingDrawn(VaultB);
            Assert.IsTrue(wall.forceRenderingOff);
            Assert.IsTrue(sign.forceRenderingOff);

            // A renderer the game turns off while the content is drawn is left alone by the next hide and show.
            InstanceScenes.KeepDrawn(VaultB);
            sign.forceRenderingOff = true;
            InstanceScenes.StopKeepingDrawn(VaultB);
            InstanceScenes.KeepDrawn(VaultB);
            Assert.IsFalse(wall.forceRenderingOff);
            Assert.IsTrue(sign.forceRenderingOff, "Nebula turns back on only what it turned off");
        }

        [Test]
        public void ContentCreatedWhileAScopeIsKeptStartsDrawn()
        {
            InstanceScenes.SetView(VaultA);
            InstanceScenes.KeepDrawn(_host.InstanceId);
            InstanceScenes.KeepDrawn(VaultB);
            MeshRenderer built = null;
            ChunkHandler build = (in ChunkContext chunk) => built = Renderer("built", chunk.Root);
            NebulaChunks.Loaded += build;
            try
            {
                var chunk = HostChunk(Vector3Int.zero, out var rock);
                Assert.IsNotNull(built);
                Assert.IsFalse(built.forceRenderingOff, "a chunk that loads while its scope is kept arrives drawn");
                Assert.IsTrue(NebulaChunks.IsDrawn(Context(chunk)));
            }
            finally { NebulaChunks.Loaded -= build; }

            Instance("b", new Vector3(10, 0, 10), out var wall);
            Assert.IsFalse(wall.forceRenderingOff, "static content prepared while its scope is kept arrives drawn");
            Instance("c", new Vector3(80, 0, 80), out var other);
            Assert.IsTrue(other.forceRenderingOff, "content of a scope nobody kept still arrives hidden");
        }

        [Test]
        public void RequestsAreCounted()
        {
            HostChunk(Vector3Int.zero, out var rock);
            InstanceScenes.SetView(VaultA);
            InstanceScenes.KeepDrawn(_host.InstanceId);
            InstanceScenes.KeepDrawn(_host.InstanceId);
            Assert.IsFalse(rock.forceRenderingOff);

            Assert.IsTrue(InstanceScenes.StopKeepingDrawn(_host.InstanceId));
            Assert.IsFalse(rock.forceRenderingOff, "one request is still live");
            Assert.IsTrue(InstanceScenes.StopKeepingDrawn(_host.InstanceId));
            Assert.IsTrue(rock.forceRenderingOff, "the last request withdrawn hides it");
            Assert.IsFalse(InstanceScenes.StopKeepingDrawn(_host.InstanceId), "an extra withdrawal changes nothing");
            Assert.IsTrue(rock.forceRenderingOff);

            InstanceScenes.KeepDrawn(_host.InstanceId);
            Assert.IsFalse(rock.forceRenderingOff, "and a new request shows it again");
        }

        [Test]
        public void AScopeChangeKeepsAKeptScopeDrawn()
        {
            HostChunk(Vector3Int.zero, out var rock);
            Instance("a", new Vector3(10, 0, 10), out var wall);
            InstanceScenes.SetView(_host.InstanceId);
            InstanceScenes.KeepDrawn(VaultA);
            InstanceScenes.KeepDrawn(_host.InstanceId);
            Assert.IsFalse(wall.forceRenderingOff, "the vault shows through the door from outside");

            // The pawn steps in: the scope it left stays drawn while it is kept.
            InstanceScenes.SetView(VaultA);
            Assert.IsFalse(rock.forceRenderingOff, "the host the pawn left is kept drawn");
            Assert.IsFalse(wall.forceRenderingOff);

            Assert.IsTrue(InstanceScenes.StopKeepingDrawn(VaultA));
            Assert.IsFalse(wall.forceRenderingOff, "the pawn's own scope is drawn with no request");
            InstanceScenes.StopKeepingDrawn(_host.InstanceId);
            Assert.IsTrue(rock.forceRenderingOff, "the door closes behind the pawn: the host is hidden");

            InstanceScenes.SetView(_host.InstanceId);
            Assert.IsFalse(rock.forceRenderingOff);
            Assert.IsTrue(wall.forceRenderingOff);
        }

        [Test]
        public void AReleaseWhileKeptCleansUp()
        {
            InstanceScenes.SetView(VaultA);
            var chunk = HostChunk(Vector3Int.zero, out _);
            var other = HostChunk(new Vector3Int(1, 0, 0), out _);
            var kept = Instance("b", new Vector3(10, 0, 10), out _);
            var hidden = Instance("c", new Vector3(80, 0, 80), out _);
            Assert.AreEqual(2, HiddenCount(typeof(InstanceScenes), "HiddenContent"));
            Assert.AreEqual(2, HiddenCount(typeof(NebulaChunks), "Hidden"));
            InstanceScenes.KeepDrawn(_host.InstanceId);
            InstanceScenes.KeepDrawn(VaultB);
            Assert.AreEqual(1, HiddenCount(typeof(InstanceScenes), "HiddenContent"));
            Assert.AreEqual(0, HiddenCount(typeof(NebulaChunks), "Hidden"));
            InstanceScenes.StopKeepingDrawn(_host.InstanceId);
            Assert.AreEqual(2, HiddenCount(typeof(NebulaChunks), "Hidden"));

            Assert.IsTrue(ContainerRegistry.UnregisterRuntime(kept.RuntimeId));
            Assert.IsTrue(ContainerRegistry.UnregisterRuntime(hidden.RuntimeId));
            Assert.IsTrue(ContainerRegistry.UnregisterRuntime(chunk.RuntimeId));
            Assert.IsTrue(ContainerRegistry.UnregisterRuntime(other.RuntimeId));
            Assert.AreEqual(0, HiddenCount(typeof(InstanceScenes), "HiddenContent"), "the released content's renderers are forgotten");
            Assert.AreEqual(0, HiddenCount(typeof(NebulaChunks), "Hidden"));
            Assert.IsTrue(InstanceScenes.StopKeepingDrawn(VaultB), "a request for a released scope is withdrawn as usual");
            Assert.AreEqual(0, HiddenCount(typeof(InstanceScenes), "HiddenContent"));

            InstanceScenes.KeepDrawn(_host.InstanceId);

            // The request outlives the content: the scope's chunks loaded again arrive drawn.
            Assert.IsTrue(InstanceScenes.IsDrawn(_host.InstanceId));
            HostChunk(Vector3Int.zero, out var rock);
            Assert.IsFalse(rock.forceRenderingOff);
            Assert.IsTrue(InstanceScenes.StopKeepingDrawn(_host.InstanceId));
            Assert.IsTrue(rock.forceRenderingOff);
        }

        [Test]
        public void ThePublicWorldIsUnchanged()
        {
            NebulaChunks.Activate(new RuntimeGrid(Cell, false), NebulaRoles.Client, false, null);
            var bounds = new Bounds(Cell * 0.5f, Cell);
            var cell = ContainerRegistry.RegisterRuntime(RuntimeGrid.PackId(Vector3Int.zero), bounds);
            ContainerRegistry.ApplyLease(cell.ContainerId, "w-1", 1, 1);
            var ground = Renderer("ground", NebulaChunks.RootOf(cell.RuntimeId));
            InstanceScenes.SetView(VaultA);
            InstanceScenes.KeepDrawn(0);
            Assert.IsFalse(InstanceScenes.StopKeepingDrawn(0), "the public world takes no request");
            Assert.IsFalse(ground.forceRenderingOff, "and is drawn from inside an instance, as it always was");
            Assert.IsTrue(InstanceScenes.IsDrawn(0));
        }

        [Test]
        public void UnusedTheViewBehavesAsBefore()
        {
            var chunk = HostChunk(Vector3Int.zero, out var rock);
            Instance("a", new Vector3(10, 0, 10), out var wallA);
            Instance("b", new Vector3(80, 0, 80), out var wallB);
            InstanceScenes.SetView(_host.InstanceId);
            Assert.IsFalse(rock.forceRenderingOff);
            Assert.IsTrue(wallA.forceRenderingOff);
            Assert.IsTrue(wallB.forceRenderingOff);
            InstanceScenes.SetView(VaultA);
            Assert.IsTrue(rock.forceRenderingOff);
            Assert.IsFalse(wallA.forceRenderingOff);
            Assert.IsTrue(wallB.forceRenderingOff);
            Assert.IsFalse(NebulaChunks.IsDrawn(Context(chunk)));
            Assert.IsTrue(InstanceScenes.IsDrawn(VaultA));
            Assert.IsFalse(InstanceScenes.IsDrawn(VaultB));
            InstanceScenes.SetView(_host.InstanceId);
            Assert.IsFalse(rock.forceRenderingOff);
            Assert.IsTrue(wallA.forceRenderingOff);
        }

        [Test]
        public void AWorkerIgnoresRequests()
        {
            NebulaRuntime.IsClient = false;
            Instance("b", new Vector3(10, 0, 10), out var wall);
            var off = Renderer("off", wall.transform.parent);
            off.forceRenderingOff = true;
            Assert.IsFalse(wall.forceRenderingOff, "a worker hides nothing");
            InstanceScenes.KeepDrawn(VaultB);
            InstanceScenes.StopKeepingDrawn(VaultB);
            Assert.IsFalse(wall.forceRenderingOff);
            Assert.IsTrue(off.forceRenderingOff);
            Assert.IsTrue(InstanceScenes.IsDrawn(VaultB), "every scope counts as drawn where nothing is hidden");
        }

        [Test]
        public void KeepingAndWithdrawingAllocatesNothingOnceWarm()
        {
            HostChunk(Vector3Int.zero, out _);
            Instance("b", new Vector3(10, 0, 10), out _);
            InstanceScenes.SetView(VaultA);
            for (int i = 0; i < 3; i++)
            {
                InstanceScenes.KeepDrawn(_host.InstanceId);
                InstanceScenes.KeepDrawn(VaultB);
                InstanceScenes.StopKeepingDrawn(_host.InstanceId);
                InstanceScenes.StopKeepingDrawn(VaultB);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                InstanceScenes.KeepDrawn(_host.InstanceId);
                InstanceScenes.KeepDrawn(VaultB);
                InstanceScenes.IsDrawn(VaultB);
                InstanceScenes.StopKeepingDrawn(_host.InstanceId);
                InstanceScenes.StopKeepingDrawn(VaultB);
            }
            Assert.AreEqual(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }
}
