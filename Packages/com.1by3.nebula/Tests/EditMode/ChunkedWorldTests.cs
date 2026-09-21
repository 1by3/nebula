using System.Collections.Generic;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// The turnkey chunked world (design <c>docs/interest-management.md</c> §10): the planar grid a surface world
    /// runs on, the single notion of "near" that sizes the allocator's ring, and the <see cref="NebulaChunks"/>
    /// content facade every role receives chunks through. The id packing is deliberately not exercised here —
    /// <c>RuntimeGridTests</c> pins it, and planar support must not have moved a bit of it.
    /// </summary>
    public sealed class ChunkedWorldTests
    {
        private const float Size = 64f;
        private const float Height = 512f;

        private WorldDefinition definition;

        [SetUp]
        public void SetUp()
        {
            definition = ScriptableObject.CreateInstance<WorldDefinition>();
            definition.CellSize = new Vector3(Size, Height, Size);
            ContainerRegistry.Rebuild();
            NebulaWorld.LoadRuntime(definition);
        }

        [TearDown]
        public void TearDown()
        {
            NebulaChunks.ResetForNewSession();
            ContainerRegistry.RuntimeBoundsInFrame = null;
            ContainerRegistry.ResetForNewSession();
            ContainerRegistry.Rebuild();
            NebulaWorld.Unload();
            Object.DestroyImmediate(definition);
        }

        private static RuntimeGrid PlanarGrid() => new RuntimeGrid(new Vector3(Size, Height, Size), planar: true);

        // ------------------------------------------------------------------ planar grid

        [Test]
        public void PlanarCoordOfIgnoresHeight()
        {
            var grid = PlanarGrid();
            Assert.AreEqual(new Vector3Int(0, 0, 0), grid.CoordOf(new Vector3(1f, 900f, 1f)), "a column has one layer, however high the point is");
            Assert.AreEqual(new Vector3Int(1, 0, -1), grid.CoordOf(new Vector3(64f, -400f, -1f)));
            Assert.AreEqual(new Vector3Int(-1, 0, 2), grid.CoordOf(new Vector3(-0.001f, 0f, 128f)));
        }

        [Test]
        public void VolumetricCoordOfStillUsesHeight()
        {
            var grid = new RuntimeGrid(new Vector3(Size, Height, Size));
            Assert.AreEqual(new Vector3Int(0, 1, 0), grid.CoordOf(new Vector3(1f, 600f, 1f)));
        }

        [Test]
        public void PlanarBoundsAreColumnsCentredOnTheGroundPlane()
        {
            var grid = PlanarGrid();
            var b = grid.BoundsOf(new Vector3Int(1, 0, -1));
            Assert.AreEqual(new Vector3(96f, 0f, -32f), b.center, "x/z from the cell, y centred on the ground plane");
            Assert.AreEqual(new Vector3(Size, Height, Size), b.size);
            // Nothing leaves a column vertically, which is the whole point of the shape.
            Assert.IsTrue(b.Contains(new Vector3(96f, 255f, -32f)));
            Assert.IsTrue(b.Contains(new Vector3(96f, -255f, -32f)));
        }

        [Test]
        public void PlanarNeighborhoodIsASquareInOneLayer()
        {
            var grid = PlanarGrid();
            var into = new List<Vector3Int>();
            grid.Neighborhood(new Vector3Int(3, 0, -2), 1, into);
            Assert.AreEqual(9, into.Count, "3x3, not 3x3x3");
            foreach (var c in into) Assert.AreEqual(0, c.y);
            Assert.Contains(new Vector3Int(2, 0, -3), into);
            Assert.Contains(new Vector3Int(4, 0, -1), into);
        }

        [Test]
        public void PlanarNeighborhoodStaysInTheLayerEvenFromAnOffLayerCentre()
        {
            var grid = PlanarGrid();
            var into = new List<Vector3Int>();
            grid.Neighborhood(new Vector3Int(0, 7, 0), 1, into);
            foreach (var c in into) Assert.AreEqual(0, c.y, "a caller's stray y must not create a second layer of leases");
        }

        [Test]
        public void VolumetricNeighborhoodIsACube()
        {
            var grid = new RuntimeGrid(Size);
            var into = new List<Vector3Int>();
            grid.Neighborhood(Vector3Int.zero, 1, into);
            Assert.AreEqual(27, into.Count);
        }

        [Test]
        public void PlanarPackingIsTheSamePackingAsBefore()
        {
            // Planar only fixes y at 0; it must not have introduced a second id format for the same cell.
            var grid = PlanarGrid();
            var coord = grid.CoordOf(new Vector3(-70f, 123f, 200f));
            Assert.AreEqual(new Vector3Int(-2, 0, 3), coord);
            Assert.AreEqual(RuntimeGrid.PackId(new Vector3Int(-2, 0, 3)), RuntimeGrid.PackId(coord));
            Assert.AreEqual(coord, RuntimeGrid.UnpackId(RuntimeGrid.PackId(coord)));
        }

        [Test]
        public void NormalizeDropsHeightOnlyWhenPlanar()
        {
            Assert.AreEqual(new Vector3Int(4, 0, 5), PlanarGrid().Normalize(new Vector3Int(4, 9, 5)));
            Assert.AreEqual(new Vector3Int(4, 9, 5), new RuntimeGrid(Size).Normalize(new Vector3Int(4, 9, 5)));
        }

        // ------------------------------------------------------------------ one notion of "near"

        [Test]
        public void AllocatorRingIsNearCellsPlusOne()
        {
            // The ring must cover the content a client will be sent entities from, plus one cell of lead so the
            // chunk exists before interest reaches into it. "What interest reaches" is not the exit radius on
            // its own: an entity only leaves after LingerSeconds beyond it, evaluated at EvalHz, so a client
            // moving at MaxFocusSpeed legitimately still holds entities that far again further out.
            var settings = InterestSettings.Default;
            settings.Radius = 120f;
            settings.ExitMargin = 16f;
            settings.ClientLoadRadiusCells = 1;
            float overshoot = settings.MaxFocusSpeed * (settings.LingerSeconds + settings.EvalInterval);
            Assert.AreEqual(15f, overshoot, 1e-3f, "12 m/s for one second of linger plus one 4 Hz interval");
            Assert.AreEqual(3, settings.NearCells(Size), "ceil((120 + 16 + 15) / 64)");
            Assert.AreEqual(4, settings.NearCells(Size) + 1);

            // nebula-virtualworld's settings: a 48 m radius on 64 m chunks needs two rings, not one, because the
            // overshoot carries interest past the single ring the exit radius alone would have asked for. This
            // is the 5x5 = 25 chunk window that sample documents.
            settings.Radius = 48f;
            Assert.AreEqual(2, settings.NearCells(Size), "ceil((48 + 16 + 15) / 64)");

            // The floor still wins when interest genuinely needs less than the game asks for.
            settings.Radius = 8f;
            settings.ExitMargin = 2f;
            settings.MaxFocusSpeed = 1f;
            settings.ClientLoadRadiusCells = 1;
            Assert.AreEqual(1, settings.NearCells(Size), "a small radius still gets ClientLoadRadiusCells as its floor");
        }

        [Test]
        public void NearCellsIsTheFloorForContentStreaming()
        {
            var settings = InterestSettings.Default;
            settings.Radius = 20f;
            settings.ExitMargin = 4f;
            settings.ClientLoadRadiusCells = 5;
            Assert.AreEqual(5, settings.NearCells(Size), "a game asking for more content than interest needs keeps it");
        }

        // ------------------------------------------------------------------ NebulaChunks

        private sealed class Recorder
        {
            public readonly List<Vector3Int> Loaded = new List<Vector3Int>();
            public readonly List<Vector3Int> Unloaded = new List<Vector3Int>();
            public readonly List<int> Seeds = new List<int>();
            public readonly List<Transform> Roots = new List<Transform>();

            public void OnLoaded(in ChunkContext c) { Loaded.Add(c.Coord); Seeds.Add(c.Seed); Roots.Add(c.Root); }
            public void OnUnloading(in ChunkContext c) => Unloaded.Add(c.Coord);
        }

        private static Container Register(RuntimeGrid grid, int x, int z) =>
            ContainerRegistry.RegisterRuntime(RuntimeGrid.PackId(new Vector3Int(x, 0, z)), grid.BoundsOf(new Vector3Int(x, 0, z)));

        [Test]
        public void LoadedAndUnloadingPairUpWithRegistration()
        {
            var grid = PlanarGrid();
            grid.UseAsRuntimeBounds();
            NebulaChunks.Activate(grid, NebulaRoles.Client, headless: true, allocator: null);
            var r = new Recorder();
            NebulaChunks.Loaded += r.OnLoaded;
            NebulaChunks.Unloading += r.OnUnloading;

            Register(grid, 0, 0);
            Register(grid, 1, 0);
            Assert.AreEqual(new[] { new Vector3Int(0, 0, 0), new Vector3Int(1, 0, 0) }, r.Loaded.ToArray());
            Assert.AreEqual(2, NebulaChunks.LoadedCount);

            ContainerRegistry.UnregisterRuntime(RuntimeGrid.PackId(new Vector3Int(1, 0, 0)));
            Assert.AreEqual(new[] { new Vector3Int(1, 0, 0) }, r.Unloaded.ToArray());
            Assert.AreEqual(1, NebulaChunks.LoadedCount);
            Assert.IsNull(NebulaChunks.RootOf(RuntimeGrid.PackId(new Vector3Int(1, 0, 0))), "the chunk's root goes with it");
        }

        [Test]
        public void LoadedBackFillsChunksThatAlreadyExist()
        {
            var grid = PlanarGrid();
            grid.UseAsRuntimeBounds();
            NebulaChunks.Activate(grid, NebulaRoles.Worker, headless: true, allocator: null);
            Register(grid, 0, 0);
            Register(grid, -1, 2);

            // The handler arrives late — a scene object's Start, a component enabled by a game mode — and must
            // still be told about every chunk, or half the world has no content for the rest of the session.
            var r = new Recorder();
            NebulaChunks.Loaded += r.OnLoaded;
            Assert.AreEqual(2, r.Loaded.Count);
            Assert.Contains(new Vector3Int(0, 0, 0), r.Loaded);
            Assert.Contains(new Vector3Int(-1, 0, 2), r.Loaded);
        }

        [Test]
        public void ActivateBackFillsChunksRegisteredBeforeItRan()
        {
            var grid = PlanarGrid();
            grid.UseAsRuntimeBounds();
            var r = new Recorder();
            NebulaChunks.Loaded += r.OnLoaded;
            Register(grid, 5, 5);
            Assert.AreEqual(0, r.Loaded.Count, "no chunked world yet: a runtime container is not a chunk");

            NebulaChunks.Activate(grid, NebulaRoles.Client, headless: false, allocator: null);
            Assert.AreEqual(new[] { new Vector3Int(5, 0, 5) }, r.Loaded.ToArray());
        }

        [Test]
        public void ContentRootIsAChildOfTheChunkAndIsDestroyedWithIt()
        {
            var grid = PlanarGrid();
            grid.UseAsRuntimeBounds();
            NebulaChunks.Activate(grid, NebulaRoles.Client, headless: false, allocator: null);
            var r = new Recorder();
            NebulaChunks.Loaded += r.OnLoaded;
            var container = Register(grid, 2, -3);

            Assert.AreEqual(1, r.Roots.Count);
            var root = r.Roots[0];
            Assert.IsNotNull(root);
            Assert.AreSame(container.transform, root.parent);
            Assert.AreEqual(Vector3.zero, root.localPosition);

            var content = new GameObject("slab");
            content.transform.SetParent(root, false);
            ContainerRegistry.UnregisterRuntime(container.RuntimeId);
            Assert.IsTrue(content == null, "content parented under the chunk root goes with the chunk");
        }

        [Test]
        public void SeedIsDeterministicPerChunkAndDiffersBetweenNeighbours()
        {
            int a = NebulaChunks.SeedOf(new Vector3Int(0, 0, 0));
            int b = NebulaChunks.SeedOf(new Vector3Int(0, 0, 1));
            int c = NebulaChunks.SeedOf(new Vector3Int(1, 0, 0));
            Assert.AreEqual(a, NebulaChunks.SeedOf(new Vector3Int(0, 0, 0)), "same chunk, same content, on every role and every run");
            Assert.AreNotEqual(a, b, "adjacent ids differ in one low bit; the mix must still separate them");
            Assert.AreNotEqual(a, c);
            Assert.AreNotEqual(b, c);
            Assert.AreEqual(NebulaChunks.SeedOf(RuntimeGrid.PackId(new Vector3Int(-4, 0, 7))), NebulaChunks.SeedOf(new Vector3Int(-4, 0, 7)));
        }

        [Test]
        public void EnsureAtCallsBackImmediatelyForAResidentChunkAndWaitsOtherwise()
        {
            var grid = PlanarGrid();
            grid.UseAsRuntimeBounds();
            NebulaChunks.Activate(grid, NebulaRoles.Client, headless: true, allocator: null);
            var here = Register(grid, 0, 0);

            Container got = null;
            NebulaChunks.EnsureAt(new Vector3(10f, 0f, 10f), c => got = c);
            Assert.AreSame(here, got);

            got = null;
            NebulaChunks.EnsureAt(new Vector3(200f, 0f, 0f), c => got = c);
            Assert.IsNull(got, "no client-side allocation: the chunk arrives when the gateway says it does");
            var later = Register(grid, 3, 0);
            Assert.AreSame(later, got);
        }

        [Test]
        public void CoordOfAndAtAgreeWithTheGrid()
        {
            var grid = PlanarGrid();
            grid.UseAsRuntimeBounds();
            NebulaChunks.Activate(grid, NebulaRoles.Client, headless: true, allocator: null);
            var c = Register(grid, -1, 0);
            Assert.AreEqual(new Vector3Int(-1, 0, 0), NebulaChunks.CoordOf(new Vector3(-10f, 30f, 5f)));
            Assert.AreSame(c, NebulaChunks.At(new Vector3(-10f, 30f, 5f)));
            Assert.IsNull(NebulaChunks.At(new Vector3(1000f, 0f, 0f)));
        }

        // ------------------------------------------------------------------ origin shifts

        [Test]
        public void OriginShiftKeepsChunkBoundsOverTheSameGround()
        {
            var grid = PlanarGrid();
            grid.UseAsRuntimeBounds();
            NebulaChunks.Activate(grid, NebulaRoles.Client, headless: true, allocator: null);
            var container = Register(grid, 10, -10);

            // A point standing in the chunk before the shift must still be in it after: the box and the entity
            // move by the same delta, which is what makes a shift invisible.
            var before = container.WorldBounds;
            Assert.AreEqual(new Vector3(672f, 0f, -608f), before.center);

            RuntimeGrid.ShiftOriginTo(new Vector3Int(10, 0, -10));
            var after = ContainerRegistry.GetRuntime(container.RuntimeId).WorldBounds;
            Assert.AreEqual(new Vector3(32f, 0f, 32f), after.center, "the chunk is now the one at the origin");
            Assert.AreEqual(before.size, after.size);
            Assert.AreEqual(new Vector3Int(10, 0, -10), grid.CoordOf(after.center), "and still the same cell of the world");
        }

        [Test]
        public void PlanarOriginNeverShiftsVertically()
        {
            var grid = PlanarGrid();
            grid.UseAsRuntimeBounds();
            grid.KeepOriginNear(new Vector3Int(40, 6, 40), ring: 1);
            Assert.AreEqual(0, WorldOrigin.Cell.y, "a column world has one layer; a vertical shift would move the ground");
            Assert.AreEqual(40, WorldOrigin.Cell.x);
            Assert.AreEqual(40, WorldOrigin.Cell.z);
        }

        [Test]
        public void KeepOriginNearDoesNothingInsideTheRing()
        {
            var grid = PlanarGrid();
            grid.UseAsRuntimeBounds();
            grid.KeepOriginNear(new Vector3Int(1, 0, -1), ring: 2);
            Assert.AreEqual(Vector3Int.zero, WorldOrigin.Cell);
        }

        [Test]
        public void ResetForNewSessionDropsSubscribersAndGrid()
        {
            var grid = PlanarGrid();
            grid.UseAsRuntimeBounds();
            NebulaChunks.Activate(grid, NebulaRoles.Client, headless: true, allocator: null);
            var r = new Recorder();
            NebulaChunks.Loaded += r.OnLoaded;
            NebulaChunks.ResetForNewSession();

            Assert.IsFalse(NebulaChunks.IsActive);
            Assert.IsNull(NebulaChunks.Grid);
            Register(grid, 0, 0);
            Assert.AreEqual(0, r.Loaded.Count, "a play session without a domain reload must not inherit the last one's handlers");
        }
    }
}
