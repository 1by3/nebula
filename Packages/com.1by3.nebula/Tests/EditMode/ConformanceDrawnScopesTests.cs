using System;
using System.Collections.Generic;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-435, scenarios 74 and 75 (<c>docs/portals.md</c> D40 to D42, <c>docs/conformance-suite.md</c>): a client draws several scopes at once, each in
    /// its own pose against the anchor, and gives each drawn scope a rendering-layer slot. Tier B (a real client's registries, transforms and
    /// renderers; no network, no physics scene). The cases: two turned scopes drawn at once from the public world sit at their doors; from inside a
    /// turned scope an open link to another scope shows it at its door and the host where the host is, at 10^8 m; the anchor's roots are never moved
    /// and everything is put back exactly; cameras are posed only when the view is not the anchor; a matrix drawn scope-by-scope agrees with the posed
    /// roots; slot bits are stamped on the managed renderers and nothing else of a mask changes; a slot is released when its scope stops being drawn.
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceDrawnScopesTests
    {
        private const string Scope = "space/drawn";
        private static readonly Vector3 SpaceCell = new Vector3(8192f, 8192f, 8192f);
        private static readonly Vector3 HallSize = new Vector3(8f, 4f, 8f);
        private static readonly Double3 Far = new Double3(100_000_000.25, -300_000.5, -100_000_000.75);
        private static readonly Quaternion TurnA = Quaternion.Euler(0f, 180f, 0f) * Quaternion.Euler(55f, 0f, 0f);
        private static readonly Quaternion TurnB = Quaternion.Euler(0f, 90f, 0f);

        private readonly List<Object> _objects = new List<Object>();
        private WorldDefinition _definition;
        private NebulaClient _client;
        private RuntimeGrid _space;
        private NebulaConfig _config, _wasConfig;
        private bool _wasClient, _wasServer;

        [SetUp]
        public void SetUp()
        {
            _wasClient = NebulaRuntime.IsClient;
            _wasServer = NebulaRuntime.IsServer;
            _wasConfig = NebulaRuntime.Config;
            NebulaRuntime.IsClient = true;
            NebulaRuntime.IsServer = false;
            _config = ScriptableObject.CreateInstance<NebulaConfig>();
            NebulaRuntime.Config = _config;
            _definition = ScriptableObject.CreateInstance<WorldDefinition>();
            _definition.CellSize = new Vector3(128f, 400f, 128f);
            NebulaWorld.LoadRuntime(_definition);
            _space = new RuntimeGrid(SpaceCell, planar: false, scopeKey: Scope);
            NebulaChunks.Activate(_space, NebulaRoles.Client, false, null);
            var go = new GameObject("client");
            _objects.Add(go);
            _client = go.AddComponent<NebulaClient>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _objects) if (o != null) Object.DestroyImmediate(o);
            _objects.Clear();
            DrawnScopes.End();
            Portals.ResetClient();
            InstanceScenes.Reset();
            DrawnScopes.ResetForNewSession();
            InstancePoses.Clear();
            NebulaChunks.ResetForNewSession();
            foreach (var c in new List<Container>(ContainerRegistry.Runtime)) if (c != null) Object.DestroyImmediate(c.gameObject);
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.RuntimeBoundsInFrame = null;
            ContainerRegistry.Rebuild();
            NebulaWorld.Unload();
            Object.DestroyImmediate(_definition);
            Object.DestroyImmediate(_config);
            NebulaRuntime.Config = _wasConfig;
            NebulaRuntime.IsClient = _wasClient;
            NebulaRuntime.IsServer = _wasServer;
        }

        // ------------------------------------------------------------------------------------------ builders

        private static double Distance(Double3 a, Double3 b) =>
            Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

        private static Double3 Turn(Quaternion q, Double3 v) => PhysicsFrames.Rotate(q, v);

        /// <summary>The world scope's chunk next to a door, with the client's origin there, and the pawn beside the door.</summary>
        private NetworkIdentity PawnInWorld(Double3 door, out Container chunk)
        {
            var coord = _space.CoordOfAbsolute(door.ToVector3());
            _space.ShiftOrigin(coord);
            var info = new InstanceContainerInfo { InstanceId = _space.InstanceId, ScopeKey = Scope, PartId = ChunkKeys.PartId(coord) };
            chunk = ContainerRegistry.RegisterRuntime(_space.IdOf(coord), _space.BoundsOf(coord), info);
            var go = new GameObject("pawn");
            _objects.Add(go);
            var pawn = go.AddComponent<NetworkIdentity>();
            pawn.SetContainer(chunk, reparent: false);
            typeof(NebulaClient).GetProperty("LocalPlayer").SetValue(_client, pawn);
            return pawn;
        }

        private Container Room(string scopeKey, Double3 center, bool hasPose, Double3 poseOrigin, Quaternion poseRotation)
        {
            var info = new InstanceContainerInfo
            {
                InstanceId = ScopeKeys.Hash(scopeKey), ScopeKey = scopeKey, PartId = "room",
                HasPose = hasPose, PoseOrigin = poseOrigin, PoseRotation = poseRotation,
            };
            _client.ShareInstanceFrame(info);
            return ContainerRegistry.RegisterRuntime(ScopeKeys.Hash(scopeKey + "/room"), ContainerPlacement.Root(center, HallSize), info);
        }

        private static PortalRecord Record(string id, ulong near, Double3 nearOrigin, Quaternion nearRotation, PortalFarKind kind, ulong far, Double3 farOrigin, Quaternion farRotation) =>
            new PortalRecord
            {
                PortalId = PortalIds.Of("container", id), Id = id, NearScope = near, NearOrigin = nearOrigin, NearRotation = nearRotation,
                FarKind = kind, FarScope = kind == PortalFarKind.Fixed ? far : 0UL, FarOrigin = farOrigin, FarRotation = farRotation,
                HalfWidth = 1.5f, HalfHeight = 2.2f, WindowMargin = 8f, WindowDepth = 48f, BandMargin = 1f, BandDepth = 2f,
            };

        private static PortalLinkRecord Link(PortalRecord portal, ulong far, PortalState state = PortalState.Open, PortalLinkFlags flags = PortalLinkFlags.Observer) =>
            new PortalLinkRecord { LinkId = PortalIds.Link(portal.PortalId, "p"), PortalId = portal.PortalId, FarScope = far, State = state, Flags = flags };

        private static void Tell(params (PortalRecord portal, PortalLinkRecord link)[] pairs)
        {
            var portals = new PortalsUpdate();
            var links = new PortalLinksUpdate();
            foreach (var pair in pairs) { portals.Upserts.Add(pair.portal); links.Upserts.Add(pair.link); }
            Portals.ApplyRecords(portals);
            Portals.ApplyLinks(links);
        }

        private Camera NewCamera(Vector3 at)
        {
            var camera = new GameObject("camera").AddComponent<Camera>();
            _objects.Add(camera.gameObject);
            camera.transform.SetPositionAndRotation(at, Quaternion.identity);
            return camera;
        }

        private static Double3 Absolute(Transform t, ulong scope) => ContainerRegistry.ToAbsolutePrecise(t.position, scope);

        // ------------------------------------------------------------------------------------------ 74: several scopes at their doors

        [Test]
        public void TwoTurnedScopesDrawnAtOnceFromThePublicWorldSitAtTheirDoors()
        {
            // The view is the public world: both rooms are drawn, one because a link the client observes leads to it, one because it is kept.
            var doorA = new Double3(1500.0, 10.0, 700.0);
            var doorB = new Double3(-900.0, 4.0, 2200.0);
            var centerA = doorA + new Double3(0, 2, 0);
            var centerB = doorB + new Double3(3, 2, -1);
            var a = Room("room/a", centerA, true, doorA, TurnA);
            var b = Room("room/b", centerB, true, doorB, TurnB);
            var recordA = Record("a", 0UL, doorA, TurnA, PortalFarKind.Template, 0UL, doorA, Quaternion.identity);
            Tell((recordA, Link(recordA, a.InstanceId)));
            InstanceScenes.KeepDrawn(b.InstanceId);
            Assert.AreEqual(0UL, Portals.Anchor, "the view is the public world, so it is the anchor");
            Assert.IsTrue(InstanceScenes.IsDrawn(a.InstanceId), "a scope the client looks through is drawn with no KeepDrawn");
            CollectionAssert.AreEquivalent(new[] { 0UL, a.InstanceId, b.InstanceId }, Portals.DrawnScopeIds);

            var atA = a.transform.position;
            var atB = b.transform.position;
            var cameraAt = new Vector3(5f, 1f, 5f);
            var camera = NewCamera(cameraAt);
            DrawnScopes.Begin(new List<Camera> { camera }, Vector3.zero);
            try
            {
                Assert.Less(Distance(Absolute(a.transform, 0UL), doorA + Turn(TurnA, centerA - doorA)), 1e-3, "room A stands behind its door");
                Assert.Less(Distance(Absolute(b.transform, 0UL), doorB + Turn(TurnB, centerB - doorB)), 1e-3, "room B stands behind its door");
                Assert.Less(Quaternion.Angle(TurnA, a.transform.rotation), 0.01f);
                Assert.Less(Quaternion.Angle(TurnB, b.transform.rotation), 0.01f);
                Assert.AreEqual(cameraAt, camera.transform.position, "the view is the anchor, so the camera is not posed");
            }
            finally { DrawnScopes.End(); }
            Assert.AreEqual(atA, a.transform.position, "put back exactly");
            Assert.AreEqual(atB, b.transform.position);
            Assert.AreEqual(Quaternion.identity, a.transform.rotation);
            Assert.AreEqual(Quaternion.identity, b.transform.rotation);

            // The pose is the composition about the door, in double.
            var pose = Portals.PoseOf(a.InstanceId);
            Assert.AreEqual(0UL, pose.Anchor);
            Assert.Less(Distance(pose.Point(centerA), doorA + Turn(TurnA, centerA - doorA)), 1e-6);
            Assert.IsTrue(Portals.PoseOf(0UL).IsIdentity);
        }

        [Test]
        public void ThePoseOfAScopeIsTheCompositionOfTheLinksFromTheAnchorAndTheAnchorsRootsNeverMove()
        {
            // The pawn stands in room H behind a turned door of the world; room V is behind a second door inside H. All at 10^8 m.
            var door = Far;
            var pawn = PawnInWorld(door, out var chunk);
            var hall = Room("room/h", door + new Double3(0, 2, 0), true, door, TurnA);
            pawn.SetContainer(hall, reparent: false);
            var doorInH = door + new Double3(6, 0, 3);
            var qNear = Quaternion.Euler(0f, 30f, 0f);
            var qFar = Quaternion.Euler(10f, 200f, 0f);
            var vaultEntrance = door + new Double3(5000, 0, 0);
            var vaultCenter = vaultEntrance + new Double3(1, 2, 7);
            var vault = Room("room/v", vaultCenter, false, Double3.Zero, Quaternion.identity);
            var hostPortal = Record("host", _space.InstanceId, door, TurnA, PortalFarKind.Template, 0UL, door, Quaternion.identity);
            var innerPortal = Record("inner", hall.InstanceId, doorInH, qNear, PortalFarKind.Fixed, vault.InstanceId, vaultEntrance, qFar);
            Tell((hostPortal, Link(hostPortal, hall.InstanceId)), (innerPortal, Link(innerPortal, vault.InstanceId)));
            InstanceScenes.SetView(hall.InstanceId);

            Assert.AreEqual(_space.InstanceId, Portals.Anchor);
            CollectionAssert.Contains(Portals.DrawnScopeIds, vault.InstanceId, "one open link away from the view");
            Assert.AreEqual(hall.InstanceId, Portals.DrawnScopeIds[0], "the view first");

            // Expected, worked independently in double: V to H through the inner door, H to the world through the host door.
            var vaultToHall = qNear * Quaternion.Inverse(qFar);
            Double3 InHall(Double3 pv) => doorInH + Turn(vaultToHall, pv - vaultEntrance);
            Double3 InWorld(Double3 ph) => door + Turn(TurnA, ph - door);
            var expectedVault = InWorld(InHall(vaultCenter));
            var expectedHall = InWorld(door + new Double3(0, 2, 0));
            var pose = Portals.PoseOf(vault.InstanceId);
            Assert.Less(Distance(pose.Point(vaultCenter), expectedVault), 1e-6, "composed in double, the pose is exact at 10^8 m");
            Assert.Less(Quaternion.Angle(pose.Turn, TurnA * vaultToHall), 0.01f);

            var hallAt = hall.transform.position;
            var vaultAt = vault.transform.position;
            var chunkAt = chunk.transform.position;
            var cameraAt = ContainerRegistry.ToFrame(door + new Double3(0, 1.6, 4), hall.InstanceId);
            var camera = NewCamera(cameraAt);
            DrawnScopes.Begin(new List<Camera> { camera }, Vector3.zero);
            try
            {
                Assert.AreEqual(chunkAt, chunk.transform.position, "the anchor's roots are never moved");
                Assert.Less(Distance(Absolute(hall.transform, _space.InstanceId), expectedHall), 2e-3, "the view's room stands at its door, the host where it is");
                Assert.Less(Distance(Absolute(vault.transform, _space.InstanceId), expectedVault), 3e-3, "the room one link further stands at its own door in H, and H's in the world");
                Assert.Less(Quaternion.Angle(TurnA * vaultToHall, vault.transform.rotation), 0.01f);
                var expectedCamera = InWorld(door + new Double3(0, 1.6, 4));
                Assert.Less(Distance(Absolute(camera.transform, _space.InstanceId), expectedCamera), 2e-3, "the camera of the view is posed with its room");
                Assert.Less(Quaternion.Angle(TurnA, camera.transform.rotation), 0.01f);
            }
            finally { DrawnScopes.End(); }
            Assert.AreEqual(hallAt, hall.transform.position, "put back exactly");
            Assert.AreEqual(vaultAt, vault.transform.position);
            Assert.AreEqual(chunkAt, chunk.transform.position);
            Assert.AreEqual(cameraAt, camera.transform.position);
            Assert.AreEqual(Quaternion.identity, camera.transform.rotation);
            Assert.AreEqual(Quaternion.identity, hall.transform.rotation);
            Assert.AreEqual(Quaternion.identity, vault.transform.rotation);
        }

        [Test]
        public void CamerasArePosedOnlyWhenTheViewIsNotTheAnchor()
        {
            var door = new Double3(2500.0, 3.0, 400.0);
            var pawn = PawnInWorld(door, out var chunk);
            var hall = Room("room/c", door + new Double3(0, 2, 0), true, door, TurnA);
            InstanceScenes.KeepDrawn(hall.InstanceId);
            var cameraAt = ContainerRegistry.ToFrame(door + new Double3(0, 1, 3), _space.InstanceId);
            var camera = NewCamera(cameraAt);
            var hallAt = hall.transform.position;

            InstanceScenes.SetView(_space.InstanceId);
            Assert.AreEqual(_space.InstanceId, Portals.Anchor);
            DrawnScopes.Begin(new List<Camera> { camera }, Vector3.zero);
            try
            {
                Assert.AreEqual(cameraAt, camera.transform.position, "the view is the anchor: the camera stays");
                Assert.AreNotEqual(hallAt, hall.transform.position, "the kept room is posed");
            }
            finally { DrawnScopes.End(); }

            pawn.SetContainer(hall, reparent: false);
            InstanceScenes.SetView(hall.InstanceId);
            DrawnScopes.Begin(new List<Camera> { camera }, Vector3.zero);
            try { Assert.AreNotEqual(cameraAt, camera.transform.position, "the view is a turned room: the camera is posed with it"); }
            finally { DrawnScopes.End(); }
            Assert.AreEqual(cameraAt, camera.transform.position);

            // A camera under a posed root is carried by it and is not posed twice.
            var parented = NewCamera(Vector3.zero);
            parented.transform.SetParent(hall.transform, false);
            var local = parented.transform.localPosition;
            DrawnScopes.Begin(new List<Camera> { parented }, Vector3.zero);
            try { Assert.AreEqual(local, parented.transform.localPosition, "not posed again by the camera pass"); }
            finally { DrawnScopes.End(); }
            Assert.AreEqual(local, parented.transform.localPosition);
        }

        [Test]
        public void ARenderMatrixAgreesWithTheRootsNebulaPosed()
        {
            var door = Far;
            var pawn = PawnInWorld(door, out _);
            var hall = Room("room/m", door + new Double3(0, 2, 0), true, door, TurnA);
            pawn.SetContainer(hall, reparent: false);
            InstanceScenes.SetView(hall.InstanceId);
            var content = new GameObject("scatter").transform;
            content.SetParent(hall.transform, false);
            content.localPosition = new Vector3(1.5f, 0.25f, -2f);
            _objects.Add(content.gameObject);
            var unposed = content.position; // an instance matrix the game keeps, in the scope's upright space
            var matrix = Portals.RenderMatrixOf(hall.InstanceId);
            var drawn = matrix.MultiplyPoint3x4(unposed);
            DrawnScopes.Begin(null, Vector3.zero);
            try
            {
                Assert.Less((content.position - drawn).magnitude, 4e-3f, "the matrix (a float one) lands where the posed root carried the child");
                Assert.Less(Quaternion.Angle(content.rotation, matrix.rotation), 0.01f);
            }
            finally { DrawnScopes.End(); }
            Assert.IsTrue(Portals.RenderMatrixOf(_space.InstanceId) == Matrix4x4.identity, "the anchor is not moved");
        }

        [Test]
        public void PosingIsRelativeToTheOffsetTheSceneWasMovedBackBy()
        {
            var door = new Double3(2500.0, 3.0, 400.0);
            PawnInWorld(door, out _);
            var hall = Room("room/o", door + new Double3(0, 2, 0), true, door, TurnA);
            InstanceScenes.KeepDrawn(hall.InstanceId);
            var posedWithoutOffset = Vector3.zero;
            DrawnScopes.Begin(null, Vector3.zero);
            try { posedWithoutOffset = hall.transform.position; }
            finally { DrawnScopes.End(); }
            var offset = new Vector3(2048f, 0f, -1024f);
            hall.transform.position -= offset; // the scene render origin moved the root
            var moved = hall.transform.position;
            DrawnScopes.Begin(null, offset);
            try { Assert.Less((hall.transform.position - (posedWithoutOffset - offset)).magnitude, 1e-3f, "the same pose, minus the offset"); }
            finally { DrawnScopes.End(); }
            Assert.AreEqual(moved, hall.transform.position);
        }

        [Test]
        public void RootSetsAreCachedPerScopeAndDroppedWhenTheRegistryChanges()
        {
            var door = new Double3(2500.0, 3.0, 400.0);
            PawnInWorld(door, out _);
            var hall = Room("room/r", door + new Double3(0, 2, 0), true, door, TurnA);
            var first = DrawnScopes.RootsOf(hall.InstanceId);
            Assert.AreSame(first, DrawnScopes.RootsOf(hall.InstanceId), "cached");
            CollectionAssert.AreEqual(new[] { hall.transform }, first);
            var info = new InstanceContainerInfo { InstanceId = hall.InstanceId, ScopeKey = "room/r", PartId = "annex", HasPose = true, PoseOrigin = door, PoseRotation = TurnA };
            var annex = ContainerRegistry.RegisterRuntime(ScopeKeys.Hash("room/r/annex"), ContainerPlacement.Root(door + new Double3(20, 2, 0), HallSize), info);
            var second = DrawnScopes.RootsOf(hall.InstanceId);
            Assert.AreNotSame(first, second, "dropped when a container registered");
            CollectionAssert.AreEquivalent(new[] { hall.transform, annex.transform }, second);
            Assert.IsTrue(ContainerRegistry.UnregisterRuntime(annex.RuntimeId));
            CollectionAssert.AreEqual(new[] { hall.transform }, DrawnScopes.RootsOf(hall.InstanceId), "and dropped again when one unregistered");
        }

        // ------------------------------------------------------------------------------------------ 75: slots

        private static Renderer NewRenderer(Transform parent, uint mask)
        {
            var go = new GameObject("mesh");
            go.transform.SetParent(parent, false);
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.renderingLayerMask = mask;
            return renderer;
        }

        [Test]
        public void SlotsAreOffByDefaultAndStampNothing()
        {
            var door = new Double3(2500.0, 3.0, 400.0);
            PawnInWorld(door, out _);
            var hall = Room("room/off", door + new Double3(0, 2, 0), true, door, TurnA);
            InstanceScenes.KeepDrawn(hall.InstanceId);
            var content = new GameObject("content");
            _objects.Add(content);
            var renderer = NewRenderer(content.transform, 0xFFu);
            InstanceScenes.AdoptContent(hall, content);
            Assert.AreEqual(0u, Portals.RenderingLayerOf(hall.InstanceId));
            Assert.AreEqual(0xFFu, renderer.renderingLayerMask);
        }

        [Test]
        public void SlotBitsAreStampedOnManagedRenderersAndNothingElseOfTheMaskChanges()
        {
            _config.ScopeRenderingLayers = 0xF0u;
            var door = new Double3(2500.0, 3.0, 400.0);
            var pawn = PawnInWorld(door, out var chunk);
            var hall = Room("room/s", door + new Double3(0, 2, 0), true, door, TurnA);
            var slotEvents = new List<(ulong scope, uint bit)>();
            Action<ulong, uint> onSlot = (scope, bit) => slotEvents.Add((scope, bit));
            Portals.SlotChanged += onSlot;
            try
            {
                InstanceScenes.SetView(_space.InstanceId);
                uint viewBit = Portals.RenderingLayerOf(_space.InstanceId);
                Assert.AreNotEqual(0u, viewBit, "the view has a slot");
                Assert.AreEqual(0u, viewBit & (viewBit - 1u), "a single bit");
                Assert.AreEqual(viewBit, viewBit & 0xF0u, "inside the reserved mask");
                uint publicBit = Portals.RenderingLayerOf(0UL);
                Assert.AreNotEqual(0u, publicBit);
                Assert.AreNotEqual(viewBit, publicBit);

                // Static content adopted while the scope is drawn is stamped: the reserved bits change, the others stay.
                InstanceScenes.KeepDrawn(hall.InstanceId);
                uint hallBit = Portals.RenderingLayerOf(hall.InstanceId);
                Assert.AreNotEqual(0u, hallBit);
                Assert.AreEqual(viewBit, Portals.RenderingLayerOf(_space.InstanceId), "the view's slot is stable while other scopes come and go");
                var content = new GameObject("content");
                _objects.Add(content);
                const uint Others = 0x00100003u;
                var inside = NewRenderer(content.transform, Others | 0x40u | 0x80u); // the game left two reserved bits on it
                InstanceScenes.AdoptContent(hall, content);
                Assert.AreEqual(Others | hallBit, inside.renderingLayerMask, "that bit, and only that bit of the reserved mask");

                // A chunk's content under its root.
                var root = NebulaChunks.RootOf(chunk.RuntimeId);
                Assert.IsNotNull(root);
                var chunkMesh = NewRenderer(root, Others);
                Portals.StampRenderingLayer(root, _space.InstanceId);
                Assert.AreEqual(Others | viewBit, chunkMesh.renderingLayerMask);

                // Entities when they spawn or change scope, cached per identity.
                var entityGo = new GameObject("crate");
                _objects.Add(entityGo);
                var entityMesh = NewRenderer(entityGo.transform, Others);
                var entity = entityGo.AddComponent<NetworkIdentity>();
                entity.SetContainer(hall, reparent: false);
                Assert.AreEqual(Others | hallBit, entityMesh.renderingLayerMask, "an entity's renderers take the slot of the scope it is placed in");
                entity.SetContainer(chunk, reparent: false);
                Assert.AreEqual(Others | viewBit, entityMesh.renderingLayerMask, "and the next one when it changes scope");

                // Another scope drawn later takes another bit; the stamped ones keep theirs.
                var other = Room("room/s2", door + new Double3(40, 2, 0), true, door + new Double3(40, 0, 0), TurnB);
                InstanceScenes.KeepDrawn(other.InstanceId);
                uint otherBit = Portals.RenderingLayerOf(other.InstanceId);
                Assert.AreNotEqual(0u, otherBit);
                Assert.AreEqual(0u, otherBit & (viewBit | publicBit | hallBit), "a bit of its own");
                Assert.AreEqual(Others | hallBit, inside.renderingLayerMask);
                Assert.IsTrue(slotEvents.Exists(e => e.scope == other.InstanceId && e.bit == otherBit), "SlotChanged told the game");
            }
            finally { Portals.SlotChanged -= onSlot; }
            Assert.IsNotNull(pawn);
        }

        [Test]
        public void ASlotIsReleasedWhenItsScopeStopsBeingDrawnAndItsRenderersLoseTheBit()
        {
            _config.ScopeRenderingLayers = 0x30u; // two slots only
            var door = new Double3(2500.0, 3.0, 400.0);
            PawnInWorld(door, out _);
            var a = Room("room/x", door + new Double3(0, 2, 0), true, door, TurnA);
            var b = Room("room/y", door + new Double3(40, 2, 0), true, door + new Double3(40, 0, 0), TurnB);
            var events = new List<(ulong scope, uint bit)>();
            Action<ulong, uint> onSlot = (scope, bit) => events.Add((scope, bit));
            Portals.SlotChanged += onSlot;
            try
            {
                InstanceScenes.SetView(0UL);
                uint publicBit = Portals.RenderingLayerOf(0UL);
                InstanceScenes.KeepDrawn(a.InstanceId);
                uint aBit = Portals.RenderingLayerOf(a.InstanceId);
                Assert.AreNotEqual(0u, aBit);
                Assert.AreEqual(0x30u, publicBit | aBit, "both slots are taken");
                var content = new GameObject("content");
                _objects.Add(content);
                var mesh = NewRenderer(content.transform, 0x1u);
                InstanceScenes.AdoptContent(a, content);
                Assert.AreEqual(0x1u | aBit, mesh.renderingLayerMask);

                // A third drawn scope finds no free bit: it draws with none, and says so once.
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("more scopes are drawn"));
                InstanceScenes.KeepDrawn(b.InstanceId);
                Assert.AreEqual(0u, Portals.RenderingLayerOf(b.InstanceId));

                events.Clear();
                Assert.IsTrue(InstanceScenes.StopKeepingDrawn(a.InstanceId));
                Assert.AreEqual(0u, Portals.RenderingLayerOf(a.InstanceId), "released");
                Assert.AreEqual(0x1u, mesh.renderingLayerMask, "its renderers lose the bit and nothing else");
                Assert.IsTrue(events.Exists(e => e.scope == a.InstanceId && e.bit == 0u));
                Assert.AreEqual(aBit, Portals.RenderingLayerOf(b.InstanceId), "the freed bit goes to the scope that was waiting");
                Assert.AreEqual(publicBit, Portals.RenderingLayerOf(0UL), "the public world keeps its slot");
            }
            finally { Portals.SlotChanged -= onSlot; }
        }

        [Test]
        public void ALinkTheClientLooksThroughEndsAndTheScopeStopsBeingDrawn()
        {
            _config.ScopeRenderingLayers = 0xF0u;
            var door = new Double3(1500.0, 10.0, 700.0);
            var a = Room("room/l", door + new Double3(0, 2, 0), true, door, TurnA);
            var record = Record("l", 0UL, door, TurnA, PortalFarKind.Template, 0UL, door, Quaternion.identity);
            Tell((record, Link(record, a.InstanceId)));
            Assert.IsTrue(InstanceScenes.IsDrawn(a.InstanceId));
            Assert.AreNotEqual(0u, Portals.RenderingLayerOf(a.InstanceId));
            Tell((record, Link(record, a.InstanceId, PortalState.Closed)));
            Assert.IsFalse(InstanceScenes.IsDrawn(a.InstanceId), "a closed link is not looked through");
            Assert.AreEqual(0u, Portals.RenderingLayerOf(a.InstanceId));
            Tell((record, Link(record, a.InstanceId, PortalState.Open, PortalLinkFlags.None)));
            Assert.IsFalse(InstanceScenes.IsDrawn(a.InstanceId), "a client that may not look through it does not draw it");
        }
    }
}
