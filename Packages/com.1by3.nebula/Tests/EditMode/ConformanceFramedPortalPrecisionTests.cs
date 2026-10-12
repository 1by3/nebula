using System;
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
    /// A <see cref="Portal"/> built in a chunk of a planet whose carrier stands 50,000 km from its scope's
    /// origin, where a float is 4 m apart: <see cref="Portal.NearFace"/> is worked out in double from the portal's frame-local
    /// position, so its origin is exact to the millimetre and does not depend on the float <c>transform.position</c> of a process.
    /// <para>Tier B, set up as <see cref="ConformanceFrameLocalPlacementTests"/>.</para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceFramedPortalPrecisionTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Scope = "space/far";
        private const string GridKey = "planet/far/0";
        private static readonly Vector3 SpaceCell = new Vector3(8192f, 8192f, 8192f);
        /// <summary>The planet's carrier, absolute: 50,000 km out, where a float is 32 m apart.</summary>
        private static readonly Vector3 PlanetAt = new Vector3(50_000_000f, 0f, 0f);
        private static readonly Vector3 PlanetBox = new Vector3(500_000f, 2_000f, 500_000f);
        private static readonly Vector3 GroundCell = new Vector3(512f, 2_000f, 512f);
        /// <summary>A pad on the planet's ground, in the planet's own coordinates: 205 km from its centre, with fractions no float there holds.</summary>
        private static readonly Double3 Pad = new Double3(204_801.234567, 1.5, -1_234.987654);
        private const double Millimetre = 1e-3;

        private ConformanceMesh _mesh;
        private LocalControlPlane _plane;
        private WorldDefinition _definition;
        private InstanceTemplate _template;
        private ushort _planetPrefab, _pawnPrefab, _shipPrefab;
        private uint _tick;
        private RuntimeGrid _ground;

        private ConformanceMesh.Worker W => _mesh[0];

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
            _definition = ScriptableObject.CreateInstance<WorldDefinition>();
            _definition.CellSize = GroundCell;
            NebulaWorld.LoadRuntime(_definition);
            _mesh = new ConformanceMesh(1);
            _mesh.Config.GhostBandMargin = -1f;
            _plane = new LocalControlPlane();
            _plane.Connect();
            typeof(NebulaWorker).GetField("<ControlPlane>k__BackingField", Flags).SetValue(W.Instance, _plane);
            var registration = (WorkerRegistration)typeof(NebulaWorker).GetField("_registration", Flags).GetValue(W.Instance);
            typeof(WorkerRegistration).GetField("<IsRegistered>k__BackingField", Flags).SetValue(registration, true);
            typeof(WorkerRegistration).GetField("_reconciledDocument", Flags).SetValue(registration, _plane.DocumentId ?? "");
            _plane.RegisterWorker(W.Id, W.Index, "127.0.0.1", 7000);
            _plane.HeartbeatWorker(W.Id, WorkerStatus.Ready, default);

            _template = ScriptableObject.CreateInstance<InstanceTemplate>();
            _template.TemplateId = "hangar";
            _template.Parts = new[] { new InstanceTemplate.Part { Id = "hall", Bounds = new Bounds(new Vector3(0f, 2f, 0f), new Vector3(16f, 4f, 16f)) } };
            _template.ObservePublic = false;

            var planet = new GameObject("planet-prefab");
            planet.AddComponent<NetworkIdentity>();
            var box = planet.AddComponent<Container>();
            box.ContainerId = "planet";
            box.Size = PlanetBox;
            box.Center = Vector3.zero;
            box.OwnPhysicsFrame = true;
            box.FrameInterest = FrameInterestMode.OwnRegions;
            planet.AddComponent<NetworkTransform>();
            _planetPrefab = _mesh.RegisterPrefab(planet);

            var ship = new GameObject("ship-prefab");
            ship.AddComponent<NetworkIdentity>();
            var interior = ship.AddComponent<Container>();
            interior.ContainerId = "ship";
            interior.Size = new Vector3(20f, 8f, 40f);
            interior.Center = new Vector3(0f, 4f, 0f);
            interior.OwnPhysicsFrame = true;
            ship.AddComponent<NetworkTransform>();
            _shipPrefab = _mesh.RegisterPrefab(ship);

            var pawn = new GameObject("pawn-prefab");
            pawn.AddComponent<NetworkIdentity>();
            _pawnPrefab = _mesh.RegisterPrefab(pawn);
            _tick = 0;
            _ground = null;
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
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
            Object.DestroyImmediate(_template);
            Object.DestroyImmediate(_definition);
        }

        private void Mirror()
        {
            ContainerRegistry.SyncRuntime(_plane.Leases);
            foreach (var lease in _plane.Leases)
                if (lease.WorkerId == W.Id) ContainerRegistry.ApplyLease(lease.ContainerId, W.Id, W.Index, lease.Epoch);
        }

        private void Tick()
        {
            W.Tick(++_tick);
            Mirror();
        }

        /// <summary>The space scope with the planet in its chunk 50,000 km out, and the planet's ground (a grid the planet hosts) with the pad's chunk leased.</summary>
        private Container Build(out NetworkIdentity planet)
        {
            var space = new RuntimeGrid(SpaceCell, planar: false, scopeKey: Scope);
            NebulaChunks.Activate(space, NebulaRoles.Worker, true, null);
            _plane.ActivateScope(new ScopeActivationRequest
            {
                ScopeKey = Scope,
                Definition = new ChunkGridDefinition { CellSize = SpaceCell, Planar = false }.ToScopeDefinition(),
                PreferredWorkerId = W.Id,
            });
            var coord = space.CoordOfAbsolute(PlanetAt);
            var spaceInstance = new InstanceContainerInfo { InstanceId = space.InstanceId, ScopeKey = Scope, PartId = ChunkKeys.PartId(coord) };
            var spaceChunk = ContainerRegistry.RegisterRuntime(space.IdOf(coord), space.BoundsOf(coord), spaceInstance);
            ContainerRegistry.ApplyLease(spaceChunk.ContainerId, W.Id, W.Index, 1);
            _plane.EnsureRuntimeContainer(spaceChunk.ContainerId, ContainerRegistry.ToAbsolute(space.BoundsOf(coord), space.InstanceId), W.Id, spaceInstance);

            NetworkIdentity p = null;
            W.Act(() =>
            {
                p = NetworkPrefabs.Instantiate(_planetPrefab, PlanetAt + space.Frame.OriginOffset, Quaternion.identity, spaceChunk.ContentRoot);
                W.Instance.SpawnServerDriven(p, spaceChunk);
            });
            planet = p;
            Assume.That(planet.Carried, Is.Not.Null, "the planet carries its box");
            Assume.That(planet.Carried.Frame, Is.Not.Null, "and its box has a frame of its own");
            Assume.That(Vector3.Distance(planet.transform.position, PlanetAt), Is.LessThan(64f), "the carrier stands 50,000 km from the scope's origin");
            return AddGroundChunk(planet, Pad);
        }

        private Container AddGroundChunk(NetworkIdentity planet, Double3 at)
        {
            if (_ground == null)
            {
                _ground = RuntimeGrid.Hosted(new ChunkGridDefinition { CellSize = GroundCell, Planar = true }, Scope, GridKey, planet.Carried.ContainerId);
                NebulaChunks.Activate(_ground, NebulaRoles.Worker, true, null);
            }
            var ground = _ground;
            var coord = ground.CoordOfAbsolute(at.ToVector3());
            var info = new InstanceContainerInfo { InstanceId = ScopeKeys.Hash(Scope), ScopeKey = Scope, PartId = ground.PartIdOf(coord) };
            _plane.EnsureRuntimeContainer(ground.ContainerIdOf(coord), ground.PlacementOf(coord), W.Id, info);
            Mirror();
            var chunk = ContainerRegistry.GetRuntime(ground.IdOf(coord));
            Assume.That(chunk, Is.Not.Null, "the pad's chunk registers under the planet");
            Assume.That(chunk.InnerSpace, Is.SameAs(planet.Carried), "in the planet's frame");
            return chunk;
        }

        private NetworkIdentity Spawn(ushort prefab, Container container, Vector3 position, Quaternion rotation)
        {
            NetworkIdentity e = null;
            W.Act(() =>
            {
                e = NetworkPrefabs.Instantiate(prefab, position, rotation, container.ContentRoot);
                e.transform.localPosition = position;
                e.transform.localRotation = rotation;
                W.Instance.SpawnServerDriven(e, container);
            });
            return e;
        }

        private static double Distance(Double3 a, Double3 b) =>
            Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

        private static Double3 InChunk(Container chunk, Double3 frameLocal) => frameLocal - chunk.transform.localPosition;

        [Test]
        public void APortalFarFromTheScopeOriginReadsItsFaceToTheMillimetre()
        {
            var chunk = Build(out var planet);
            var go = new GameObject("far door");
            go.transform.SetParent(chunk.ContentRoot, false);
            go.transform.localPosition = (InChunk(chunk, Pad)).ToVector3();
            var portal = go.AddComponent<Portal>();
            try
            {
                Assume.That(portal.NearScope, Is.EqualTo(chunk.InstanceId));
                // Independent of the code under test: the carrier in double, plus the pad (the planet is not turned).
                var carrier = planet.ScopePositionPrecise;
                var expected = ContainerRegistry.ToAbsolutePrecise(carrier + Pad, chunk.InstanceId);
                var actual = portal.NearFace.Origin;
                TestContext.WriteLine($"face error {Distance(expected, actual):E3} m");
                Assert.Less(Distance(expected, actual), Millimetre + 5e-4, "the face's origin is exact to the millimetre it is rounded to");
                Assert.AreEqual(Math.Round(expected.X, 3), actual.X, 1.5e-3, "origin x");
                Assert.AreEqual(Math.Round(expected.Y, 3), actual.Y, 1.5e-3, "origin y");
                Assert.AreEqual(Math.Round(expected.Z, 3), actual.Z, 1.5e-3, "origin z");
                // The float read this replaces was metres off at this distance.
                var viaFloat = ContainerRegistry.ToAbsolutePrecise(PhysicsFrames.ToScope(go.transform.position, chunk.InnerSpace), chunk.InstanceId);
                Assert.Greater(Distance(expected, viaFloat), 0.05, "the float path was not precise here, so the test discriminates");
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
