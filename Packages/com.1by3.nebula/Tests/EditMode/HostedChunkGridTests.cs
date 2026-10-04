using System.Collections.Generic;
using System.Reflection;
using Nebula.World;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// Chunk grids hosted by a container (<c>docs/container-tree.md</c> D22): the part ids and container ids of their
    /// chunks, their definition's new fields, the setting row that carries them to every worker, how
    /// <see cref="NebulaChunks"/> tells a hosted chunk from a root one, and the allocator's lead, reach and stale-row
    /// rules. The planet lap over a hosted grid is conformance scenario 41 (<see cref="ConformanceCarrierHostedGridTests"/>).
    /// </summary>
    public sealed class HostedChunkGridTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Scope = "space/7";
        private const string GridKey = "planet/7/2";

        private static ChunkGridDefinition Ground => new ChunkGridDefinition
        {
            CellSize = new Vector3(256f, 4096f, 256f),
            Planar = true,
            Ring = 1,
        };

        // ------------------------------------------------------------------------------------ keys

        [Test]
        public void AHostedPartIdNamesItsGridAndItsChunk()
        {
            var coord = new Vector3Int(-3, 0, 12);
            string part = ChunkKeys.HostedPartId(GridKey, coord);
            Assert.AreEqual("planet/7/2/c/-3/0/12", part);
            Assert.That(ChunkKeys.TryParseHostedPartId(part, out var key, out var parsed), Is.True);
            Assert.AreEqual(GridKey, key);
            Assert.AreEqual(coord, parsed);
            Assert.AreEqual(ScopeKeys.Hash(part), ChunkKeys.RuntimeId(GridKey, coord), "the container id is the hash of the part id");
            Assert.AreEqual(ChunkKeys.RuntimeId(GridKey, coord), RuntimeGrid.Hosted(Ground, Scope, GridKey, "planet#9").IdOf(coord),
                "and what the grid names the chunk by, whatever the scope and the host");
        }

        [TestCase("c/1/0/2")]
        [TestCase("/c/1/0/2")]
        [TestCase("planet/c/x/0/2")]
        [TestCase("planet/1/0/2")]
        [TestCase("interior")]
        [TestCase("")]
        public void OtherPartIdsAreNotHostedChunks(string part)
        {
            Assert.That(ChunkKeys.TryParseHostedPartId(part, out _, out _), Is.False);
        }

        [Test]
        public void ARootChunksPartIdIsNotReadAsHostedAndAHostedOneIsNotReadAsRoot()
        {
            Assert.That(ChunkKeys.TryParsePartId(ChunkKeys.HostedPartId(GridKey, Vector3Int.one), out _), Is.False);
            Assert.That(ChunkKeys.TryParseHostedPartId(ChunkKeys.PartId(Vector3Int.one), out _, out _), Is.False);
        }

        // ------------------------------------------------------------------------------------ definition

        [Test]
        public void LeadAndReachRoundTripAndAreLeftOutWhenUnset()
        {
            var plain = new ChunkGridDefinition { CellSize = new Vector3(64f, 512f, 64f), Planar = true, Ring = 2 };
            StringAssert.DoesNotContain("lead", plain.ToJson(), "a scope row written before these existed reads back unchanged");
            StringAssert.DoesNotContain("reach", plain.ToJson());
            var hosted = new ChunkGridDefinition { CellSize = new Vector3(64f, 512f, 64f), Planar = true, LeadSeconds = 2f, Reach = 1500f };
            var back = ChunkGridDefinition.FromJson(hosted.ToJson());
            Assert.AreEqual(2f, back.LeadSeconds);
            Assert.AreEqual(1500f, back.Reach);
            Assert.IsNotNull(new ChunkGridDefinition { LeadSeconds = -1f }.Validate());
            Assert.IsNotNull(new ChunkGridDefinition { Reach = -1f }.Validate());
        }

        [Test]
        public void TheSettingRowCarriesTheScopeTheHostAndTheGrid()
        {
            var definition = Ground;
            definition.LeadSeconds = 2f;
            string row = HostedGridSetting.Write(Scope, "planet#17", definition);
            Assert.That(HostedGridSetting.TryRead(row, out var scope, out var host, out var back), Is.True);
            Assert.AreEqual(Scope, scope);
            Assert.AreEqual("planet#17", host);
            Assert.AreEqual(definition.ToJson(), back.ToJson());
            Assert.That(HostedGridSetting.TryRead("", out _, out _, out _), Is.False, "a cleared row hosts nothing");
            Assert.AreEqual("nebula.hostedGrid/" + GridKey, HostedGridSetting.KeyOf(GridKey));
            Assert.AreEqual(GridKey, HostedGridSetting.GridKeyOf(HostedGridSetting.KeyOf(GridKey)));
            Assert.IsNotNull(HostedGridSetting.Problem("", GridKey, "planet#17", definition), "the public world hosts no grids");
            Assert.IsNotNull(HostedGridSetting.Problem(Scope, Scope, "planet#17", definition), "a grid key of its own");
            Assert.IsNotNull(HostedGridSetting.Problem(Scope, GridKey, "", definition));
            Assert.IsNull(HostedGridSetting.Problem(Scope, GridKey, "planet#17", definition));
        }

        [Test]
        public void ActivatingAHostedGridIsOneSettingRowAndIdempotent()
        {
            var plane = new LocalControlPlane();
            plane.Connect();
            try
            {
                NebulaChunkedWorld.ActivateHostedGrid(plane, Scope, GridKey, "planet#17", Ground);
                plane.Tick();
                string row = plane.GetSetting(HostedGridSetting.KeyOf(GridKey));
                Assert.That(HostedGridSetting.TryRead(row, out _, out var host, out _), Is.True);
                Assert.AreEqual("planet#17", host);
                int changes = 0;
                plane.Changed += () => changes++;
                NebulaChunkedWorld.ActivateHostedGrid(plane, Scope, GridKey, "planet#17", Ground);
                plane.Tick();
                Assert.AreEqual(0, changes, "the same activation again writes nothing");
                NebulaChunkedWorld.ActivateHostedGrid(plane, Scope, GridKey, "planet#30", Ground);
                Assert.That(HostedGridSetting.TryRead(plane.GetSetting(HostedGridSetting.KeyOf(GridKey)), out _, out host, out _), Is.True);
                Assert.AreEqual("planet#30", host, "a carrier back under a new id is hosted again by it");
                NebulaChunkedWorld.DeactivateHostedGrid(plane, GridKey);
                Assert.That(HostedGridSetting.TryRead(plane.GetSetting(HostedGridSetting.KeyOf(GridKey)), out _, out _, out _), Is.False);
                UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("not activated"));
                NebulaChunkedWorld.ActivateHostedGrid(plane, "", GridKey, "planet#30", Ground);
            }
            finally { plane.Dispose(); }
        }

        // ------------------------------------------------------------------------------------ grids

        [Test]
        public void AHostedGridPlacesItsChunksUnderItsHostInTheHostsCoordinates()
        {
            var grid = RuntimeGrid.Hosted(Ground, Scope, GridKey, "planet#17");
            Assert.That(grid.IsHosted, Is.True);
            Assert.AreEqual(Scope, grid.ScopeKey);
            Assert.AreEqual(GridKey, grid.GridKey);
            Assert.AreEqual(ScopeKeys.Hash(Scope), grid.InstanceId, "its chunks are in the scope the host lives in");
            var placement = grid.PlacementOf(new Vector3Int(-2, 5, 3));
            Assert.AreEqual("planet#17", placement.ParentId);
            Assert.AreEqual(ContainerAuthority.Leased, placement.Authority);
            Assert.AreEqual(new Vector3(-384f, 0f, 896f), placement.Center.ToVector3(), "a column centred on the host's y = 0, the vertical dropped");
            Assert.AreEqual(Ground.CellSize, placement.Size);
            Assert.AreEqual(ChunkKeys.HostedPartId(GridKey, new Vector3Int(-2, 0, 3)), grid.PartIdOf(new Vector3Int(-2, 5, 3)));
            Assert.That(grid.Frame.IsFollowing, Is.True, "its frame follows the host's");
            Assert.AreEqual(Vector3.zero, grid.Frame.OriginOffset, "with no host here, the host's coordinates are taken as they are");

            var root = new RuntimeGrid(Ground.CellSize, true, Scope);
            Assert.That(root.IsHosted, Is.False);
            Assert.AreEqual(Scope, root.GridKey);
            Assert.That(root.PlacementOf(Vector3Int.zero).IsRoot, Is.True);
        }

        [Test]
        public void AHostedGridAdoptsOnlyItsOwnChunks()
        {
            var grid = RuntimeGrid.Hosted(Ground, Scope, GridKey, "planet#17");
            var root = new RuntimeGrid(Ground.CellSize, true, Scope);
            var other = RuntimeGrid.Hosted(Ground, Scope, "planet/7/3", "planet#18");
            var coord = new Vector3Int(4, 0, -1);
            ulong id = ChunkKeys.RuntimeId(GridKey, coord);
            Assert.That(root.Adopt(id, ChunkKeys.HostedPartId(GridKey, coord), out _), Is.False, "the scope's root grid does not take it");
            Assert.That(other.Adopt(id, ChunkKeys.HostedPartId(GridKey, coord), out _), Is.False, "nor does another hosted grid");
            Assert.That(grid.Adopt(id, ChunkKeys.PartId(coord), out _), Is.False, "a root part id is not this grid's");
            Assert.That(grid.Adopt(id, ChunkKeys.HostedPartId(GridKey, coord), out var adopted), Is.True);
            Assert.AreEqual(coord, adopted);
            Assert.That(grid.TryFindCoordNear(ChunkKeys.RuntimeId(GridKey, new Vector3Int(6, 0, 0)), Vector3Int.zero, 6, out var found), Is.True);
            Assert.AreEqual(new Vector3Int(6, 0, 0), found);
        }

        [Test]
        public void NebulaChunksTellsAHostedChunkFromARootChunkOfTheSameScope()
        {
            try
            {
                var root = new RuntimeGrid(new Vector3(2048f, 2048f, 2048f), false, Scope);
                NebulaChunks.Activate(root, NebulaRoles.Worker, true, null);
                // A fixed host is enough for the lookups; a planet's is a carrier's container with a frame of its own.
                var host = ContainerRegistry.RegisterRuntime(1UL, new Bounds(Vector3.zero, new Vector3(2048f, 2048f, 2048f)),
                    new InstanceContainerInfo { InstanceId = root.InstanceId, ScopeKey = Scope, PartId = "host" });
                var hosted = RuntimeGrid.Hosted(Ground, Scope, GridKey, host.ContainerId);
                NebulaChunks.Activate(hosted, NebulaRoles.Worker, true, null);
                Assert.AreSame(root, NebulaChunks.GridFor(Scope));
                Assert.AreSame(hosted, NebulaChunks.GridFor(GridKey), "by its own key");
                CollectionAssert.AreEqual(new[] { hosted }, NebulaChunks.HostedGridsIn(Scope));

                // Rows in from elsewhere: neither grid has named these chunks, so each must be told apart by its part id.
                var coord = new Vector3Int(1, 0, 1);
                var chunk = ContainerRegistry.RegisterRuntime(ChunkKeys.RuntimeId(GridKey, coord),
                    ContainerPlacement.Child(host.ContainerId, hosted.AbsoluteCenterOf(coord), Ground.CellSize, ContainerAuthority.Leased),
                    new InstanceContainerInfo { InstanceId = hosted.InstanceId, ScopeKey = Scope, PartId = ChunkKeys.HostedPartId(GridKey, coord) });
                var rootChunk = ContainerRegistry.RegisterRuntime(ChunkKeys.RuntimeId(Scope, coord), ContainerPlacement.Root(Double3.From(new Vector3(3072f, 3072f, 3072f)), root.CellSize),
                    new InstanceContainerInfo { InstanceId = root.InstanceId, ScopeKey = Scope, PartId = ChunkKeys.PartId(coord) });
                Assert.IsNotNull(chunk);
                Assert.AreSame(root, NebulaChunks.GridOf(rootChunk));
                Assert.AreSame(hosted, NebulaChunks.GridOf(chunk));
                Assert.AreEqual(Scope, NebulaChunks.ScopeOf(chunk), "a hosted chunk is in its host's scope");
                Assert.AreSame(hosted, NebulaChunks.GridOf(chunk.RuntimeId));
                // What holds a hosted chunk is its host's carrier chain, down to the scope's root container: a client
                // standing in one keeps its scope's root grid's origin. No root grid knows the host's container here.
                Assert.IsNull(NebulaChunks.GridHolding(chunk));

                NebulaChunks.Deactivate(GridKey);
                Assert.IsNull(NebulaChunks.GridFor(GridKey));
                Assert.IsNull(NebulaChunks.GridOf(chunk));
                Assert.AreSame(root, NebulaChunks.GridFor(Scope), "forgetting a hosted grid leaves the scope's own");
            }
            finally
            {
                NebulaChunks.ResetForNewSession();
                ContainerRegistry.PruneRuntime(new HashSet<ulong>());
                ContainerRegistry.Rebuild();
            }
        }

        // ------------------------------------------------------------------------------------ the allocator

        /// <summary>One worker, a planet carrier with a frame in a scope's space, a grid hosted by it and a client's pawn.</summary>
        private sealed class Fixture : System.IDisposable
        {
            public ConformanceMesh Mesh;
            public LocalControlPlane Plane;
            public NetworkIdentity Planet, Pawn;
            public RuntimeGrid Grid;
            public RuntimeGridAllocator Allocator;
            public Container Space;
            public float Seconds;
            private readonly float _bucketSize;
            private readonly System.DateTime _start = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);

            public Fixture(float lead, float reach)
            {
                PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
                PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
                Mesh = new ConformanceMesh(1);
                Plane = new LocalControlPlane { Clock = () => _start.AddSeconds(Seconds) };
                Plane.Connect();
                var w = Mesh[0];
                typeof(NebulaWorker).GetField("<ControlPlane>k__BackingField", Flags).SetValue(w.Instance, Plane);
                var registration = (WorkerRegistration)typeof(NebulaWorker).GetField("_registration", Flags).GetValue(w.Instance);
                typeof(WorkerRegistration).GetField("<IsRegistered>k__BackingField", Flags).SetValue(registration, true);
                typeof(WorkerRegistration).GetField("_reconciledDocument", Flags).SetValue(registration, Plane.DocumentId ?? "");

                var planet = new GameObject("planet-prefab");
                planet.AddComponent<NetworkIdentity>();
                var box = planet.AddComponent<Container>();
                box.ContainerId = "planet";
                box.Size = new Vector3(4096f, 1000f, 4096f);
                box.Center = Vector3.zero;
                box.OwnPhysicsFrame = true;
                box.FrameInterest = FrameInterestMode.OwnRegions;
                planet.AddComponent<NetworkTransform>();
                ushort planetPrefab = Mesh.RegisterPrefab(planet);
                var pawn = new GameObject("pawn-prefab");
                pawn.AddComponent<NetworkIdentity>();
                ushort pawnPrefab = Mesh.RegisterPrefab(pawn);

                _bucketSize = ContainerRegistry.RuntimeBucketSize;
                ContainerRegistry.RuntimeBucketSize = 4096f; // the space below is one runtime box 40 km across
                Space = ContainerRegistry.RegisterRuntime(5UL, new Bounds(Vector3.zero, new Vector3(40000f, 40000f, 40000f)),
                    new InstanceContainerInfo { InstanceId = ScopeKeys.Hash(Scope), ScopeKey = Scope, PartId = "space" });
                ContainerRegistry.ApplyLease(Space.ContainerId, w.Id, w.Index, 1);
                Planet = w.SpawnServerDriven(planetPrefab, Space, new Vector3(10000f, 0f, 0f), Quaternion.identity);
                var definition = new ChunkGridDefinition { CellSize = new Vector3(256f, 1000f, 256f), Planar = true, Ring = 1, LeadSeconds = lead, Reach = reach };
                Grid = RuntimeGrid.Hosted(definition, Scope, GridKey, Planet.Carried.ContainerId);
                Allocator = new RuntimeGridAllocator(w.Instance, Grid) { TickIntervalSeconds = 0f, Ring = 1, LeadSeconds = lead, Reach = reach };
                Pawn = w.SpawnServerDriven(pawnPrefab, Space, Vector3.zero, Quaternion.identity);
                Pawn.OwnerClientId = 7;
            }

            /// <summary>Put the pawn at a point in the planet's own coordinates (it stays in the space container: the allocator reads its position).</summary>
            public void PawnAt(Vector3 local) => Pawn.transform.position = Planet.transform.TransformPoint(local);

            public void Tick(float seconds)
            {
                Seconds = seconds;
                Mesh[0].Act(() => Allocator.Tick(seconds));
            }

            public HashSet<Vector3Int> Wanted()
            {
                var cells = new HashSet<Vector3Int>();
                foreach (var id in Allocator.WantedIds) if (Grid.TryCoordOf(id, out var c)) cells.Add(c);
                return cells;
            }

            public void Dispose()
            {
                Mesh.Dispose();
                Plane.Dispose();
                ContainerRegistry.PruneRuntime(new HashSet<ulong>());
                ContainerRegistry.Rebuild();
                ContainerRegistry.RuntimeBucketSize = _bucketSize;
                PhysicsFrames.DrainPool();
                PhysicsFrames.SceneFactory = null;
                PhysicsFrames.SceneDisposer = null;
            }
        }

        [Test]
        public void AllocatorRequestsHostedChunksAsChildrenOfTheHostWithTheirGridInThePartId()
        {
            using (var f = new Fixture(0f, 0f))
            {
                f.PawnAt(new Vector3(300f, 1f, -10f));
                f.Tick(0f);
                CollectionAssert.AreEquivalent(new[]
                {
                    new Vector3Int(0, 0, -2), new Vector3Int(1, 0, -2), new Vector3Int(2, 0, -2),
                    new Vector3Int(0, 0, -1), new Vector3Int(1, 0, -1), new Vector3Int(2, 0, -1),
                    new Vector3Int(0, 0, 0), new Vector3Int(1, 0, 0), new Vector3Int(2, 0, 0),
                }, f.Wanted(), "a ring of one around the pawn's cell in the planet's coordinates, wherever the planet is");
                var row = f.Plane.FindLease(f.Grid.ContainerIdOf(new Vector3Int(1, 0, -1)));
                Assert.IsNotNull(row);
                Assert.AreEqual(f.Planet.Carried.ContainerId, row.ParentId);
                Assert.AreEqual(ContainerAuthority.Leased, row.Authority);
                Assert.AreEqual(new Vector3(384f, 0f, -128f), row.Center.ToVector3());
                Assert.AreEqual(Scope, row.Instance.ScopeKey);
                Assert.AreEqual(ChunkKeys.HostedPartId(GridKey, new Vector3Int(1, 0, -1)), row.Instance.PartId);
                Assert.AreEqual(f.Mesh[0].Id, row.WorkerId, "assigned to the worker that asked");
            }
        }

        [Test]
        public void OnlyPawnsInsideTheHostsBoxOrWithinReachCount()
        {
            using (var f = new Fixture(0f, 1000f))
            {
                f.PawnAt(new Vector3(0f, 1400f, 0f)); // 900 m above the box's top
                f.Tick(0f);
                Assert.AreEqual(9, f.Wanted().Count, "within reach above the planet");
                f.PawnAt(new Vector3(0f, 1600f, 0f)); // 1,100 m above it
                f.Tick(1f);
                Assert.AreEqual(0, f.Wanted().Count, "beyond reach, nothing");
                f.PawnAt(new Vector3(3200f, 0f, 0f)); // 1,152 m past the box's side
                f.Tick(2f);
                Assert.AreEqual(0, f.Wanted().Count);
            }
        }

        [Test]
        public void AMovingPawnLeasesACapsuleAlongItsVelocity()
        {
            using (var f = new Fixture(2f, 0f))
            {
                f.PawnAt(new Vector3(10f, 1f, 10f));
                f.Tick(0f);
                Assert.AreEqual(9, f.Wanted().Count, "no velocity yet: the ring");
                // 300 m/s along +x for a quarter of a second.
                f.PawnAt(new Vector3(85f, 1f, 10f));
                f.Tick(0.25f);
                var wanted = f.Wanted();
                Assert.That(wanted.Contains(new Vector3Int(2, 0, 0)), Is.True, "the cell 600 m ahead");
                Assert.That(wanted.Contains(new Vector3Int(3, 0, 0)), Is.True, "and the ring around it");
                Assert.That(wanted.Contains(new Vector3Int(3, 0, 1)), Is.True);
                Assert.That(wanted.Contains(new Vector3Int(-2, 0, 0)), Is.False, "and nothing more behind");
                Assert.That(wanted.Contains(new Vector3Int(0, 0, 3)), Is.False, "or to the side");
                Assert.AreEqual(15, wanted.Count, "three wide, five long: a capsule, not the 7 x 7 a ring would need");
                // A jump is not travel.
                f.PawnAt(new Vector3(1500f, 1f, 10f));
                f.Tick(0.5f);
                Assert.AreEqual(9, f.Wanted().Count);
            }
        }

        [Test]
        public void AChunkRowLeftUnderAHostThatIsGoneIsReplaced()
        {
            using (var f = new Fixture(0f, 0f))
            {
                var coord = new Vector3Int(0, 0, 0);
                string containerId = f.Grid.ContainerIdOf(coord);
                f.Plane.EnsureRuntimeContainer(containerId, ContainerPlacement.Child("planet#9999", f.Grid.AbsoluteCenterOf(coord), f.Grid.CellSize, ContainerAuthority.Leased),
                    f.Mesh[0].Id, new InstanceContainerInfo { InstanceId = f.Grid.InstanceId, ScopeKey = Scope, PartId = f.Grid.PartIdOf(coord) });
                Container ready = null;
                f.Mesh[0].Act(() => f.Allocator.EnsureContainer(coord, c => ready = c));
                Assert.AreEqual(f.Planet.Carried.ContainerId, f.Plane.FindLease(containerId).ParentId, "re-requested under the host that is here");
                ContainerRegistry.SyncRuntime(f.Plane.Leases);
                Assert.IsNotNull(ready);
                Assert.AreSame(f.Planet.Carried, ready.Parent);
            }
        }

        [Test]
        public void NothingIsRequestedWhileTheHostIsNotHere()
        {
            using (var f = new Fixture(0f, 0f))
            {
                var stray = RuntimeGrid.Hosted(Ground, Scope, "planet/7/5", "planet#4242");
                var allocator = new RuntimeGridAllocator(f.Mesh[0].Instance, stray) { TickIntervalSeconds = 0f };
                f.PawnAt(Vector3.zero);
                f.Mesh[0].Act(() => allocator.Tick(0f));
                Assert.AreEqual(0, allocator.WantedIds.Count);
            }
        }

        [Test]
        public void APlanarHostedGridKeepsItsHostsOriginLevel()
        {
            using (var f = new Fixture(0f, 0f))
            {
                Assert.IsNotNull(f.Grid.Host);
                Assert.That(f.Planet.Carried.Frame.KeepOriginLevel, Is.True);
                int shifts = 0;
                f.Grid.Frame.Shifted += _ => shifts++;
                var frame = f.Planet.Carried.Frame;
                PhysicsFrames.AutoShift((fr, points) => { if (fr == frame) points.Add(new Vector3(5000f, 3000f, 0f)); });
                Assert.AreEqual(new Vector3(5120f, 0f, 0f), frame.Origin, "moved across, never up");
                Assert.AreEqual(1, shifts, "the grid's frame reports its host's shift");
                Assert.AreEqual(new Vector3(-5120f, 0f, 0f), f.Grid.Frame.OriginOffset);
                Assert.AreEqual(new Vector3Int(1, 0, 0), f.Grid.CoordOf(frame.LocalToSimulation(new Vector3(300f, 0f, 10f))), "positions in simulation space are read through it");
                Assert.AreEqual(frame.LocalToSimulation(new Vector3(384f, 0f, 128f)), f.Grid.CenterOf(new Vector3Int(1, 0, 0)));
            }
        }

        /// <summary>Two cells whose boxes overlap by 200 m, split at x = 0: the shape of neighbouring cells on a curved surface.</summary>
        private sealed class OverlappingCells : IChunkGridGeometry
        {
            public Vector3Int CellOf(Vector3 local) => new Vector3Int(local.x < 0f ? 0 : 1, 0, 0);
            public Bounds BoundsOf(Vector3Int cell) => new Bounds(new Vector3(cell.x == 0 ? -200f : 200f, 0f, 0f), new Vector3(600f, 100f, 600f));
            public Vector3Int Normalize(Vector3Int cell) => new Vector3Int(cell.x, 0, 0);
            public void Neighborhood(Vector3Int cell, int ring, List<Vector3Int> into) { into.Add(new Vector3Int(0, 0, 0)); into.Add(new Vector3Int(1, 0, 0)); }
            public ulong IdOf(string gridKey, Vector3Int cell) => ChunkKeys.RuntimeId(gridKey, Normalize(cell));
            public float LeadStep => 100f;
        }

        [Test]
        public void AGeometryOfTheGamesOwnPlacesItsCellsAndDecidesWhichOneHoldsAPoint()
        {
            using (var f = new Fixture(0f, 0f))
            {
                try
                {
                    var grid = RuntimeGrid.Hosted(Ground, Scope, "faces/7", f.Planet.Carried.ContainerId, new OverlappingCells());
                    Assert.That(grid.Planar, Is.False, "a geometry of the game's own is not a lattice of columns");
                    NebulaChunks.Activate(grid, NebulaRoles.Worker, true, null);
                    var allocator = new RuntimeGridAllocator(f.Mesh[0].Instance, grid) { TickIntervalSeconds = 0f, Reach = 1000f };
                    f.Allocator = allocator;
                    f.PawnAt(new Vector3(-50f, 0f, 0f));
                    f.Tick(0f);
                    Assert.AreEqual(2, allocator.WantedIds.Count, "the ring is the geometry's neighbourhood");
                    var row = f.Plane.FindLease(grid.ContainerIdOf(Vector3Int.zero));
                    Assert.AreEqual(new Vector3(-200f, 0f, 0f), row.Center.ToVector3(), "placed at its own box");
                    Assert.AreEqual(new Vector3(600f, 100f, 600f), row.BoundsSize);
                    ContainerRegistry.SyncRuntime(f.Plane.Leases);
                    var west = ContainerRegistry.GetRuntime(grid.IdOf(Vector3Int.zero));
                    var east = ContainerRegistry.GetRuntime(grid.IdOf(new Vector3Int(1, 0, 0)));
                    Assert.IsNotNull(west);
                    Assert.IsNotNull(east);
                    var frame = f.Planet.Carried.Frame;
                    // Both boxes hold x = -50 and x = +50; the geometry says which cell each point is in.
                    Assert.AreSame(west, ContainerRegistry.FindInSpace(frame.LocalToSimulation(new Vector3(-50f, 0f, 0f)), null, grid.InstanceId, null, f.Planet.Carried));
                    Assert.AreSame(east, ContainerRegistry.FindInSpace(frame.LocalToSimulation(new Vector3(50f, 0f, 0f)), null, grid.InstanceId, null, f.Planet.Carried));
                    Assert.AreEqual(new Vector3Int(1, 0, 0), grid.CoordOf(frame.LocalToSimulation(new Vector3(50f, 0f, 0f))));
                }
                finally { NebulaChunks.ResetForNewSession(); }
            }
        }
    }
}
