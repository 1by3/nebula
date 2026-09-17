using Nebula.World;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    public sealed class RuntimeOriginTests
    {
        private WorldDefinition definition;
        [SetUp] public void SetUp()
        {
            definition = ScriptableObject.CreateInstance<WorldDefinition>();
            definition.CellSize = Vector3.one * 512;
            NebulaWorld.LoadRuntime(definition);
        }
        [TearDown] public void TearDown()
        {
            ContainerRegistry.RuntimeBoundsInFrame = null;
            ContainerRegistry.Rebuild();
            NebulaWorld.Unload();
            Object.DestroyImmediate(definition);
        }
        [Test] public void RuntimeWorldShiftsContainersWithoutAuthoredScenes()
        {
            ContainerRegistry.RuntimeBoundsInFrame = (id, bounds) => new Bounds(
                new Vector3((float)(((long)id - WorldOrigin.Cell.x + .5) * 512), 0, 0), Vector3.one * 512);
            var container = ContainerRegistry.RegisterRuntime(1000000, new Bounds(Vector3.zero, Vector3.one * 512));
            var child = new GameObject("local pose");
            child.transform.SetParent(container.transform, false);
            child.transform.localPosition = new Vector3(.125f, 1, -.375f);
            for (int i = 0; i < 5; i++)
            {
                NebulaWorld.Streamer.ShiftOrigin(new Vector3Int(1000000, 0, 0));
                Assert.AreEqual(new Vector3(256.125f, 1, -.375f), child.transform.position);
                NebulaWorld.Streamer.ShiftOrigin(new Vector3Int(-1000000, 0, 0));
                Assert.AreEqual(new Vector3(.125f, 1, -.375f), child.transform.localPosition);
            }
            Assert.AreSame(definition, NebulaWorld.Definition);
            Assert.IsFalse(NebulaWorld.IsActive, "No authored-scene gate is enabled");
            Assert.IsTrue(NebulaWorld.IsContentLoaded(container));
        }
        [Test] public void NewRuntimeContainerResolvesInShiftedFrame()
        {
            NebulaWorld.Streamer.ShiftOrigin(new Vector3Int(1000000, 0, 0));
            ContainerRegistry.RuntimeBoundsInFrame = (id, bounds) => new Bounds(new Vector3(256, 0, 256), bounds.size);
            var c = ContainerRegistry.RegisterRuntime(123, new Bounds(new Vector3(512000000, 0, 0), Vector3.one * 512));
            Assert.AreEqual(new Vector3(256, 0, 256), c.transform.position);
        }
        [Test] public void UnloadClearsOriginForTheNextSession()
        {
            NebulaWorld.Streamer.ShiftOrigin(new Vector3Int(1000000, 0, -1000000));
            NebulaWorld.Unload();
            Assert.AreEqual(Vector3Int.zero, WorldOrigin.Cell);
            Assert.IsNull(WorldOrigin.Definition);
            Assert.AreEqual(0, WorldOrigin.ShiftCount);
        }
        [Test] public void OppositeSignedCellExtremesDoNotOverflow()
        {
            var a = new Vector3Int(1500000000, 0, -1500000000);
            var b = -a;
            var delta = WorldOrigin.ShiftDelta(definition, a, b);
            Assert.Greater(delta.x, 0);
            Assert.Less(delta.z, 0);
            Assert.AreEqual(delta, definition.FrameOrigin(a, b));
        }
    }
}