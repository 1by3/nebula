using System.Collections.Generic;
using System.Reflection;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-340, ported to <see cref="Portal"/> (NEB-428): a portal placed in a scoped grid's chunk content (a planet whose content the
    /// game builds at runtime) lets a player into its instance and back out into the grid, on a worker whose frame for
    /// that grid has moved away from absolute coordinates. Tier B: one real <see cref="NebulaWorker"/> on the
    /// <see cref="ConformanceMesh"/>, with its real tick running the portal, a <see cref="LocalControlPlane"/> the
    /// instance is activated on, and the grid's own origin frame. The owning client's half of readiness (its
    /// <see cref="MsgId.PortalWarm"/>) is stood in for by a message from a gateway.
    /// </summary>
    public sealed class PortalScopedGridTests
    {
        private const string Planet = "world/planet";
        private const string Moon = "world/moon";
        private const string Owner = "player-a";
        private static readonly Vector3 Cell = new Vector3(64f, 512f, 64f);
        /// <summary>6.4 km out: the frame the worker follows is far from absolute coordinates once it shifts here.</summary>
        private static readonly Vector3Int Far = new Vector3Int(100, 0, 100);
        /// <summary>Where the portal stands in its chunk.</summary>
        private static readonly Vector3 DoorAt = new Vector3(10f, 0f, 10f);

        private ConformanceMesh _mesh;
        private LocalControlPlane _plane;
        private WorldDefinition _definition;
        private RuntimeGrid _planet, _moon;
        private InstanceTemplate _template;
        private ushort _pawnPrefab;
        private uint _tick;
        private readonly List<Portal> _portals = new List<Portal>();
        private ConformanceMesh.Gateway _gateway;

        private ConformanceMesh.Worker W => _mesh[0];

        [SetUp]
        public void SetUp()
        {
            _definition = ScriptableObject.CreateInstance<WorldDefinition>();
            _definition.CellSize = Cell;
            NebulaWorld.LoadRuntime(_definition); // the public frame, which an instance lives in
            _mesh = new ConformanceMesh(1);
            _gateway = _mesh.AddGateway("gw");
            _mesh.LinkGateway(_gateway);
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
            foreach (var b in _portals)
                if (b != null) typeof(Portal).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(b, null);
            _portals.Clear();
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
            Portals.ResetForNewSession();
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

        /// <summary>A portal in a chunk's content, the way a game builds it: under a content root parented to the chunk, with the vault as its far side.</summary>
        private Portal Door(Container chunk)
        {
            var root = new GameObject("content");
            root.transform.SetParent(chunk.transform, false);
            var go = new GameObject("vault door");
            go.transform.SetParent(root.transform, false);
            go.transform.position = chunk.WorldBounds.min + new Vector3(0f, -chunk.WorldBounds.min.y, 0f) + DoorAt;
            var portal = go.AddComponent<Portal>();
            portal.Id = "vault-door";
            portal.FarTemplate = _template;
            portal.Aperture = PortalAperture.Box(2f, 2.5f);
            // Edit mode runs no OnEnable for a plain MonoBehaviour: register it as play mode would.
            typeof(Portal).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(portal, null);
            _portals.Add(portal);
            return portal;
        }

        /// <summary>Open the door for the player's party and run the ticks that get it ready: activated, content ready, client warm.</summary>
        private PortalLink Open(Portal portal)
        {
            var link = portal.Open(Owner, new ulong[] { 7 });
            Tick();
            Tick();
            _mesh.FromGateway(_gateway, W, w => new PortalWarmMsg { LinkId = link.Id, Generation = link.Generation, ClientId = 7 }.Write(w));
            Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Open), link.ToString());
            return link;
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
            // The control plane's lease rows reach the registry, and their owners, as the worker's registration does every poll.
            ContainerRegistry.SyncRuntime(_plane.Leases);
            foreach (var lease in _plane.Leases)
                if (lease.WorkerId == W.Id) ContainerRegistry.ApplyLease(lease.ContainerId, W.Id, W.Index, lease.Epoch);
        }

        private static Double3 Absolute(NetworkIdentity entity) => ContainerRegistry.ToAbsolutePrecise(entity.transform.position, entity.InstanceId);

        private static void AreClose(Double3 expected, Double3 actual, string message)
        {
            Assert.AreEqual(expected.X, actual.X, 1e-2, message + " (x)");
            Assert.AreEqual(expected.Y, actual.Y, 1e-2, message + " (y)");
            Assert.AreEqual(expected.Z, actual.Z, 1e-2, message + " (z)");
        }

        [Test]
        public void APortalParentedUnderAChunkStandsInThatChunksScope()
        {
            var chunk = Chunk(_planet, Far);
            var portal = Door(chunk);
            Assert.AreEqual(_planet.InstanceId, portal.NearScope);
            Assert.AreSame(chunk, portal.NearContainer, "fixed in the chunk it is built in");
            Assert.AreEqual(PortalIds.Of(chunk.ContainerId, "vault-door"), portal.PortalId);
            var loose = new GameObject("scene door").AddComponent<Portal>();
            try { Assert.AreEqual(0UL, loose.NearScope, "a portal in the shared scene stands in the public world, as before"); }
            finally { Object.DestroyImmediate(loose.gameObject); }
        }

        [Test]
        public void APlayerOnAScopedGridEntersTheInstanceAndLeavesBackIntoTheGrid()
        {
            var chunk = Chunk(_planet, Far);
            _planet.ShiftOrigin(Far); // this worker's frame for the planet is now 6.4 km from absolute coordinates
            Assume.That(chunk.transform.position.magnitude, Is.LessThan(Cell.x), "the planet's frame follows the chunk");
            var portal = Door(chunk);
            var door = portal.NearFace.Origin;
            Assert.Greater(door.X, 6000.0, "the portal's absolute position is the planet's, not its frame numbers");

            var player = Player(chunk, portal.transform.position + new Vector3(0f, 1f, -6f));
            var link = Open(portal); // activated at the portal's absolute position, ready, warm
            var hall = ContainerRegistry.GetRuntime(NebulaWorker.InstanceKey("vault/" + Owner + "/hall"));
            Assert.That(hall, Is.Not.Null, "its entry part is registered");
            var lease = _plane.FindLease(hall.ContainerId);
            AreClose(door + new Vector3(0f, 2f, 0f), Double3.From(lease.Bounds.center), "the instance stands at the door in absolute coordinates");

            player.transform.position = portal.transform.position + new Vector3(0f, 1f, 1f);
            Tick(); // through the opening: committed

            Assert.AreSame(hall, player.Container, "the player crossed into the instance");
            Assert.AreEqual(NebulaWorker.InstanceKey("vault/" + Owner), player.InstanceId);
            AreClose(door + new Vector3(0f, 1f, 1f), Absolute(player), "at the same absolute position");

            // Walking back out.
            player.transform.position += new Vector3(0f, 0f, -1.5f);
            Tick();
            Assert.AreSame(chunk, player.Container, "back in the planet's chunk, not the public world");
            Assert.AreEqual(_planet.InstanceId, player.InstanceId);
            AreClose(door + new Vector3(0f, 1f, -0.5f), Absolute(player), "where it walked out, through the planet's frame");
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
        }

        [Test]
        public void APortalInTheSharedSceneStillCrossesFromAndBackToThePublicWorld()
        {
            var street = _mesh.AddStaticContainer("street", Vector3.zero, new Vector3(200f, 50f, 200f));
            _mesh.SetOwner(street, W);
            var go = new GameObject("apartment door");
            go.transform.position = new Vector3(20f, 0f, 20f);
            var portal = go.AddComponent<Portal>();
            portal.Id = "apartment";
            portal.FarTemplate = _template;
            portal.Aperture = PortalAperture.Box(2f, 2.5f);
            typeof(Portal).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(portal, null);
            _portals.Add(portal);
            try
            {
                var player = Player(street, go.transform.position + new Vector3(0f, 1f, -6f));
                Open(portal);
                player.transform.position = go.transform.position + new Vector3(0f, 1f, 1f);
                Tick();
                Assert.AreEqual(NebulaWorker.InstanceKey("vault/" + Owner), player.InstanceId, "entered, as before NEB-340");
                Assert.AreEqual(go.transform.position + new Vector3(0f, 1f, 1f), player.transform.position, "at the same world position");

                player.transform.position += new Vector3(0f, 0f, -1.5f);
                Tick();
                Assert.AreSame(street, player.Container, "and back out into the public container");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void APortalIgnoresPlayersOnAnotherScopeAtTheSameNumbers()
        {
            var chunk = Chunk(_planet, Far);
            var moonChunk = Chunk(_moon, Far);
            var portal = Door(chunk);
            Assume.That(moonChunk.transform.position, Is.EqualTo(chunk.transform.position), "both worlds' frames put the chunks at the same numbers");
            var link = Open(portal);
            var player = Player(moonChunk, portal.transform.position + new Vector3(0f, 1f, -6f));
            Tick();
            player.transform.position = portal.transform.position + new Vector3(0f, 1f, 1f);
            Tick();

            Assert.AreSame(moonChunk, player.Container, "nobody on the moon walks through a door of the planet");
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
        }

        [Test]
        public void TheFaceOriginIsTheSameWhateverTheFrame()
        {
            var chunk = Chunk(_planet, Far);
            var portal = Door(chunk);
            var before = portal.NearFace.Origin;
            _planet.ShiftOrigin(Far);
            var after = portal.NearFace.Origin;
            Assert.AreEqual(before, after, "rounded to a millimetre, the origin survives the frame moving 6.4 km");
        }
    }
}
