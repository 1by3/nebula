using System.Collections.Generic;
using System.Reflection;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// The client half of <c>docs/scope-frames.md</c> D4b (NEB-338): a client whose pawn is in a scoped chunk grid sends
    /// its focus hint in absolute coordinates of that scope. The grid moves its own origin, not the public one, so a
    /// hint converted through the public origin landed where the shifted frame happened to put it.
    /// </summary>
    public sealed class ClientFocusHintScopeFrameTests
    {
        private static readonly Vector3 Cell = new Vector3(256f, 256f, 256f);
        private static readonly Vector3Int Far = new Vector3Int(7, 0, 4);

        private WorldDefinition _definition;
        private RuntimeGrid _grid;
        private GameObject _clientHost;
        private GameObject _pawn;
        private NebulaClient _client;

        [SetUp]
        public void SetUp()
        {
            _definition = ScriptableObject.CreateInstance<WorldDefinition>();
            _definition.CellSize = Cell;
            NebulaWorld.LoadRuntime(_definition);
            _grid = new RuntimeGrid(Cell, planar: true, scopeKey: "world/planet");
            NebulaChunks.Activate(_grid, NebulaRoles.Worker, true, null);
            _clientHost = new GameObject("client");
            _client = _clientHost.AddComponent<NebulaClient>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_pawn != null) Object.DestroyImmediate(_pawn);
            Object.DestroyImmediate(_clientHost);
            NebulaChunks.ResetForNewSession();
            foreach (var c in new List<Container>(ContainerRegistry.Runtime)) if (c != null) Object.DestroyImmediate(c.gameObject);
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.RuntimeBoundsInFrame = null;
            ContainerRegistry.Rebuild();
            NebulaWorld.Unload();
            Object.DestroyImmediate(_definition);
        }

        [Test]
        public void AFocusHintInAScopedGridIsSentInThatScopesAbsoluteCoordinates()
        {
            var chunk = ContainerRegistry.RegisterRuntime(_grid.IdOf(Far), _grid.BoundsOf(Far), new InstanceContainerInfo
            {
                InstanceId = _grid.InstanceId,
                ScopeKey = _grid.ScopeKey,
                PartId = ChunkKeys.PartId(Far),
            });
            _pawn = new GameObject("pawn");
            var identity = _pawn.AddComponent<NetworkIdentity>();
            identity.Container = chunk;
            typeof(NebulaClient).GetProperty(nameof(NebulaClient.LocalPlayer), BindingFlags.Instance | BindingFlags.Public)
                .SetValue(_client, identity);

            _grid.KeepOriginNear(Far, 1);
            Assert.IsTrue(ScopeFrames.HasFrame(_grid.InstanceId), "the grid has a frame of its own");
            Assert.AreNotEqual(Vector3.zero, ScopeFrames.Of(_grid.InstanceId).OriginOffset, "and its origin moved");

            // A point in the middle of the far chunk: absolute (1920, 0, 1152).
            var absolute = new Vector3(Far.x * Cell.x + 128f, 0f, Far.z * Cell.z + 128f);
            _client.FocusHint = ContainerRegistry.ToFrame(Double3.From(absolute), _grid.InstanceId);
            Assert.AreNotEqual(absolute, _client.FocusHint.Value, "the frame position is not the absolute one");

            _client.AbsoluteFocusHint(out double x, out double y, out double z);
            Assert.AreEqual(absolute.x, x, 1e-3, "x");
            Assert.AreEqual(absolute.y, y, 1e-3, "y");
            Assert.AreEqual(absolute.z, z, 1e-3, "z");
        }

        [Test]
        public void AFocusHintInThePublicWorldIsUnchanged()
        {
            _client.FocusHint = new Vector3(10f, 2f, -30f);
            _client.AbsoluteFocusHint(out double x, out double y, out double z);
            Assert.AreEqual(10.0, x, 1e-6);
            Assert.AreEqual(2.0, y, 1e-6);
            Assert.AreEqual(-30.0, z, 1e-6);
        }
    }
}
