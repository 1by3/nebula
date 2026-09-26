using System.Collections.Generic;
using System.Reflection;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-340: an <see cref="InstanceBoundary"/> placed in a scoped grid's chunk content (a planet whose content the
    /// game builds at runtime) lets a player into its instance and back out into the grid, on a worker whose frame for
    /// that grid has moved away from absolute coordinates. Tier B: one real <see cref="NebulaWorker"/> on the
    /// <see cref="ConformanceMesh"/>, with its real tick running the boundary, a <see cref="LocalControlPlane"/> the
    /// instance is activated on, and the grid's own origin frame. The client's half of a crossing (the gateway's
    /// acknowledgement) is stood in for by marking the preparation client-ready.
    /// </summary>
    public sealed class InstanceBoundaryScopedGridTests
    {
        private const string Planet = "world/planet";
        private const string Moon = "world/moon";
        private const string Owner = "player-a";
        private static readonly Vector3 Cell = new Vector3(64f, 512f, 64f);
        /// <summary>6.4 km out: the frame the worker follows is far from absolute coordinates once it shifts here.</summary>
        private static readonly Vector3Int Far = new Vector3Int(100, 0, 100);
        /// <summary>Where the boundary stands in its chunk.</summary>
        private static readonly Vector3 Door = new Vector3(10f, 0f, 10f);

        private ConformanceMesh _mesh;
        private LocalControlPlane _plane;
        private WorldDefinition _definition;
        private RuntimeGrid _planet, _moon;
        private InstanceTemplate _template;
        private ushort _pawnPrefab;
        private uint _tick;

        private ConformanceMesh.Worker W => _mesh[0];

        [SetUp]
        public void SetUp()
        {
            _definition = ScriptableObject.CreateInstance<WorldDefinition>();
            _definition.CellSize = Cell;
            NebulaWorld.LoadRuntime(_definition); // the public frame, which an instance lives in
            _mesh = new ConformanceMesh(1);
            _plane = new LocalControlPlane();
            _plane.Connect();
            AttachRegistered(W.Instance, _plane);
            _plane.RegisterWorker(W.Id, W.Index, "127.0.0.1", 7000);
            _plane.HeartbeatWorker(W.Id, WorkerStatus.Ready, default);
            _planet = new RuntimeGrid(Cell, planar: true, scopeKey: Planet);
            _moon = new RuntimeGrid(Cell, planar: true, scopeKey: Moon);
            foreach (var grid in new[] { _planet, _moon })
            {
                NebulaChunks.Activate(grid, NebulaRoles.Worker, true, null);
                _plane.ActivateScope(new ScopeActivationRequest
                {
                    ScopeKey = grid.ScopeKey,
                    Definition = new ChunkGridDefinition { CellSize = Cell, Planar = true }.ToScopeDefinition(),
                    PreferredWorkerId = W.Id,
                });
            }
            _template = ScriptableObject.CreateInstance<InstanceTemplate>();
            _template.TemplateId = "vault";
            _template.Parts = new[] { new InstanceTemplate.Part { Id = "hall", Bounds = new Bounds(new Vector3(0f, 2f, 0f), new Vector3(8f, 4f, 8f)) } };
            _template.ObservePublic = false;
            var pawn = new GameObject("pawn-prefab");
            pawn.AddComponent<NetworkIdentity>();
            _pawnPrefab = _mesh.RegisterPrefab(pawn);
            _tick = 0;
        }

        [TearDown]
        public void TearDown()
        {
            _mesh.Dispose();
            _plane.Dispose();
            NebulaChunks.ResetForNewSession();
            foreach (var c in new List<Container>(ContainerRegistry.Runtime)) if (c != null) Object.DestroyImmediate(c.gameObject);
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.RuntimeBoundsInFrame = null;
            ContainerRegistry.Rebuild();
            NebulaWorld.Unload();
            Object.DestroyImmediate(_template);
            Object.DestroyImmediate(_definition);
        }

        private static void AttachRegistered(NebulaWorker worker, LocalControlPlane plane)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(NebulaWorker).GetField("<ControlPlane>k__BackingField", flags).SetValue(worker, plane);
            var registration = (WorkerRegistration)typeof(NebulaWorker).GetField("_registration", flags).GetValue(worker);
            typeof(WorkerRegistration).GetField("<IsRegistered>k__BackingField", flags).SetValue(registration, true);
            typeof(WorkerRegistration).GetField("_reconciledDocument", flags).SetValue(registration, plane.DocumentId ?? "");
        }

        /// <summary>One chunk of a grid, owned by the worker: registered and leased on the control plane.</summary>
        private Container Chunk(RuntimeGrid grid, Vector3Int coord)
        {
            var instance = new InstanceContainerInfo { InstanceId = grid.InstanceId, ScopeKey = grid.ScopeKey, PartId = ChunkKeys.PartId(coord) };
            var chunk = ContainerRegistry.RegisterRuntime(grid.IdOf(coord), grid.BoundsOf(coord), instance);
            ContainerRegistry.ApplyLease(chunk.ContainerId, W.Id, W.Index, 1);
            _plane.EnsureRuntimeContainer(chunk.ContainerId, ContainerRegistry.ToAbsolute(grid.BoundsOf(coord), grid.InstanceId), W.Id, instance);
            return chunk;
        }

        /// <summary>A boundary in a chunk's content, the way a game builds it: under a content root parented to the chunk.</summary>
        private InstanceBoundary Boundary(Container chunk)
        {
            var root = new GameObject("content");
            root.transform.SetParent(chunk.transform, false);
            var go = new GameObject("vault door");
            go.transform.SetParent(root.transform, false);
            go.transform.position = chunk.WorldBounds.min + new Vector3(0f, -chunk.WorldBounds.min.y, 0f) + Door;
            var boundary = go.AddComponent<InstanceBoundary>();
            boundary.Template = _template;
            boundary.Interior = new Bounds(new Vector3(0f, 2f, 0f), new Vector3(8f, 4f, 8f));
            boundary.PreparationDistance = 6f;
            return boundary;
        }

        private NetworkIdentity Player(Container chunk, Vector3 position)
        {
            var player = W.SpawnServerDriven(_pawnPrefab, chunk, position, Quaternion.identity);
            player.OwnerClientId = 7;
            player.OwnerIdentity = Owner;
            return player;
        }

        private void Tick()
        {
            W.Tick(++_tick);
            // The control plane's lease rows reach the registry, as the worker's registration does every poll.
            ContainerRegistry.SyncRuntime(_plane.Leases);
        }

        /// <summary>Stand in for the owning client's gateway acknowledging every preparation in flight.</summary>
        private void AcknowledgeClients()
        {
            var field = typeof(NebulaWorker).GetField("_instanceTransfers", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var transfer in ((Dictionary<uint, InstanceTransfer>)field.GetValue(W.Instance)).Values) transfer.ClientReady = true;
        }

        private static Double3 Absolute(NetworkIdentity entity) => ContainerRegistry.ToAbsolutePrecise(entity.transform.position, entity.InstanceId);

        private static void AreClose(Double3 expected, Double3 actual, string message)
        {
            Assert.AreEqual(expected.X, actual.X, 1e-2, message + " (x)");
            Assert.AreEqual(expected.Y, actual.Y, 1e-2, message + " (y)");
            Assert.AreEqual(expected.Z, actual.Z, 1e-2, message + " (z)");
        }

        [Test]
        public void ABoundaryParentedUnderAChunkStandsInThatChunksScope()
        {
            var chunk = Chunk(_planet, Far);
            var boundary = Boundary(chunk);
            Assert.AreEqual(_planet.InstanceId, boundary.HostInstanceId);
            var loose = new GameObject("scene door").AddComponent<InstanceBoundary>();
            try { Assert.AreEqual(0UL, loose.HostInstanceId, "a boundary in the shared scene stands in the public world, as before"); }
            finally { Object.DestroyImmediate(loose.gameObject); }
        }

        [Test]
        public void APlayerOnAScopedGridEntersTheInstanceAndLeavesBackIntoTheGrid()
        {
            var chunk = Chunk(_planet, Far);
            _planet.ShiftOrigin(Far); // this worker's frame for the planet is now 6.4 km from absolute coordinates
            Assume.That(chunk.transform.position.magnitude, Is.LessThan(Cell.x), "the planet's frame follows the chunk");
            var boundary = Boundary(chunk);
            var door = boundary.AbsolutePosition;
            Assert.Greater(door.X, 6000.0, "the boundary's absolute position is the planet's, not its frame numbers");

            var player = Player(chunk, boundary.transform.position + new Vector3(0f, 1f, -8f));
            Tick(); // near the door: the instance is activated at the boundary's absolute position
            var scope = _plane.FindScope("vault/" + Owner);
            Assert.That(scope, Is.Not.Null, "approaching the door prepares the player's instance");
            var hall = ContainerRegistry.GetRuntime(NebulaWorker.InstanceKey("vault/" + Owner + "/hall"));
            Assert.That(hall, Is.Not.Null, "its entry part is registered");
            var lease = _plane.FindLease(hall.ContainerId);
            AreClose(door + new Vector3(0f, 2f, 0f), Double3.From(lease.Bounds.center), "the instance stands at the door in absolute coordinates");

            Tick(); // the crossing is prepared
            AcknowledgeClients();
            player.transform.position = boundary.transform.position + new Vector3(0f, 1f, 1f);
            Tick(); // inside the volume: committed

            Assert.AreSame(hall, player.Container, "the player crossed into the instance");
            Assert.AreEqual(NebulaWorker.InstanceKey("vault/" + Owner), player.InstanceId);
            AreClose(door + new Vector3(0f, 1f, 1f), Absolute(player), "at the same absolute position");

            // Walking back out: the first attempt is held until the crossing is ready, then it commits.
            player.transform.position += new Vector3(0f, 0f, -7f);
            Tick();
            Assert.AreSame(hall, player.Container, "not ready yet: held inside");
            AreClose(door + new Vector3(0f, 1f, 1f), Absolute(player), "held where it last stood");
            AcknowledgeClients();
            player.transform.position += new Vector3(0f, 0f, -7f);
            Tick();

            Assert.AreSame(chunk, player.Container, "back in the planet's chunk, not the public world");
            Assert.AreEqual(_planet.InstanceId, player.InstanceId);
            AreClose(door + new Vector3(0f, 1f, -6f), Absolute(player), "where it walked out, through the planet's frame");
        }

        [Test]
        public void ABoundaryIgnoresPlayersOnAnotherScopeAtTheSameNumbers()
        {
            var chunk = Chunk(_planet, Far);
            var moonChunk = Chunk(_moon, Far);
            var boundary = Boundary(chunk);
            Assume.That(moonChunk.transform.position, Is.EqualTo(chunk.transform.position), "both worlds' frames put the chunks at the same numbers");
            var player = Player(moonChunk, boundary.transform.position + new Vector3(0f, 1f, 0f));

            Tick();
            Tick();

            Assert.That(_plane.FindScope("vault/" + Owner), Is.Null, "no instance is prepared for a player on the moon");
            Assert.AreSame(moonChunk, player.Container);
        }

        [Test]
        public void TheInstanceOriginIsTheSameWhateverTheFrame()
        {
            var chunk = Chunk(_planet, Far);
            var boundary = Boundary(chunk);
            var before = boundary.InstanceOrigin;
            _planet.ShiftOrigin(Far);
            var after = boundary.InstanceOrigin;
            Assert.AreEqual(before, after, "rounded to a millimetre, the origin survives the frame moving 6.4 km");
        }
    }
}
