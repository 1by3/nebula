using System;
using System.Collections.Generic;
using System.Reflection;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-426, the client's half (<c>docs/scope-frames.md</c> D11): a client whose pawn stands in a scope far from the
    /// public origin (a planet's door 5×10^7 m out) registers the instance behind the door in <b>that scope's</b> frame,
    /// so the interior is drawn exactly where it is relative to the world around the door before the crossing, the
    /// crossing does not move the pawn in Unity, the world outside is drawn where it is from inside, and the client keeps
    /// that frame's origin near its pawn while it is inside, so both stay near Unity's origin.
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceFarInstanceClientFrameTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Scope = "space/client-far";
        private const string InstanceScope = "vault/client-far";
        private static readonly Vector3 SpaceCell = new Vector3(8192f, 8192f, 8192f);
        private static readonly Double3 Door = new Double3(50_000_320.437, -279_999.75, -59_999_807.189);
        private static readonly Vector3 HallSize = new Vector3(8f, 4f, 8f);
        private const double Millimetre = 1e-3;
        /// <summary>Within one 8 km space cell's diagonal of Unity's origin: the client keeps its origin in its pawn's cell.</summary>
        private const float NearOrigin = 16_000f;

        private readonly List<Object> _objects = new List<Object>();
        private WorldDefinition _definition;
        private NebulaClient _client;
        private RuntimeGrid _space;
        private bool _wasClient, _wasServer;

        [SetUp]
        public void SetUp()
        {
            _wasClient = NebulaRuntime.IsClient;
            _wasServer = NebulaRuntime.IsServer;
            NebulaRuntime.IsClient = true;
            NebulaRuntime.IsServer = false;
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
            NebulaChunks.ResetForNewSession();
            foreach (var c in new List<Container>(ContainerRegistry.Runtime)) if (c != null) Object.DestroyImmediate(c.gameObject);
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.RuntimeBoundsInFrame = null;
            ContainerRegistry.Rebuild();
            NebulaWorld.Unload();
            Object.DestroyImmediate(_definition);
            NebulaRuntime.IsClient = _wasClient;
            NebulaRuntime.IsServer = _wasServer;
        }

        private static double Distance(Double3 a, Double3 b) =>
            Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

        /// <summary>The door's space chunk, with the client's origin there (as it keeps it near its pawn), and the pawn beside the door.</summary>
        private NetworkIdentity PawnAtTheDoor(out Container chunk)
        {
            var coord = _space.CoordOfAbsolute(Door.ToVector3());
            _space.ShiftOrigin(coord);
            var info = new InstanceContainerInfo { InstanceId = _space.InstanceId, ScopeKey = Scope, PartId = ChunkKeys.PartId(coord) };
            chunk = ContainerRegistry.RegisterRuntime(_space.IdOf(coord), _space.BoundsOf(coord), info);
            var go = new GameObject("pawn");
            _objects.Add(go);
            var pawn = go.AddComponent<NetworkIdentity>();
            pawn.SetContainer(chunk, reparent: false);
            pawn.transform.position = ContainerRegistry.ToFrame(Door + new Double3(0, 1, -2), _space.InstanceId);
            typeof(NebulaClient).GetProperty("LocalPlayer").SetValue(_client, pawn);
            return pawn;
        }

        /// <summary>The instance's row reaching the client, as its container-ownership update registers it.</summary>
        private Container Hall()
        {
            var info = new InstanceContainerInfo { InstanceId = ScopeKeys.Hash(InstanceScope), ScopeKey = InstanceScope, PartId = "hall" };
            _client.ShareInstanceFrame(info);
            return ContainerRegistry.RegisterRuntime(ScopeKeys.Hash(InstanceScope + "/hall"), ContainerPlacement.Root(Door + new Double3(0, 2, 0), HallSize), info);
        }

        [Test]
        public void TheClientDrawsTheInstanceWhereItIsRelativeToTheWorldAroundTheDoor()
        {
            var pawn = PawnAtTheDoor(out _);
            var hall = Hall();
            Assert.IsTrue(ScopeFrames.IsShared(hall.InstanceId), "the instance shares the frame of the scope the pawn enters it from");
            Assert.Less(hall.transform.position.magnitude, NearOrigin, "near Unity's origin, where the client keeps its pawn");
            // The door's point, drawn through the host's frame and through the instance's: one place.
            var viaHost = ContainerRegistry.ToFrame(Door, _space.InstanceId);
            var viaInstance = ContainerRegistry.ToFrame(Door, hall.InstanceId);
            Assert.AreEqual(viaHost, viaInstance, "the interior is drawn exactly where it is behind the door");
            Assert.Less(Distance(Door + new Double3(0, 2, 0), ContainerRegistry.ToAbsolutePrecise(hall.transform.position, hall.InstanceId)), Millimetre);
            Assert.IsNotNull(pawn);
        }

        [Test]
        public void CrossingDoesNotMoveThePawnAndTheOutsideStaysWhereItIs()
        {
            var pawn = PawnAtTheDoor(out var chunk);
            var hall = Hall();
            var before = pawn.transform.position;
            var outsideBefore = chunk.transform.position;
            var absBefore = ContainerRegistry.ToAbsolutePrecise(pawn.transform.position, pawn.InstanceId);
            pawn.SetContainer(hall, reparent: false); // the commit reaches the client
            Assert.AreEqual(hall.InstanceId, pawn.InstanceId);
            Assert.AreEqual(before, pawn.transform.position, "no jump in Unity at the switch");
            Assert.Less(Distance(absBefore, ContainerRegistry.ToAbsolutePrecise(pawn.transform.position, pawn.InstanceId)), Millimetre,
                "and the same absolute point");
            Assert.AreEqual(outsideBefore, chunk.transform.position, "the world outside is drawn where it was");
            Assert.AreSame(_space, NebulaChunkedWorld.OriginGridOf(pawn, null), "inside, the client follows the host scope's origin");

            // Walk 20 km inside the instance (a long dungeon): the client moves that origin with its pawn, and the
            // instance, the pawn and the world outside move together.
            pawn.transform.position += new Vector3(20_000f, 0f, 0f);
            var pawnCell = _space.CoordOf(pawn.transform.position);
            int shifts = _space.Frame.ShiftCount;
            _space.KeepOriginNear(pawn, 1);
            Assert.Greater(_space.Frame.ShiftCount, shifts, "the origin followed the pawn into the instance");
            Assert.AreEqual(pawnCell, _space.Frame.Cell, "to the pawn's cell");
            Assert.Less(Distance(Door + new Double3(0, 2, 0), ContainerRegistry.ToAbsolutePrecise(hall.transform.position, hall.InstanceId)), Millimetre,
                "the instance moved with it");
            Assert.AreEqual(ContainerRegistry.ToFrame(Door, _space.InstanceId), ContainerRegistry.ToFrame(Door, hall.InstanceId), "still drawn together");
        }

        [Test]
        public void WithoutAPawnInAnotherScopeTheInstanceIsPinnedAndItsFrameGoesWithIt()
        {
            var hall = Hall();
            Assert.IsFalse(ScopeFrames.IsShared(hall.InstanceId));
            Assert.IsTrue(ScopeFrames.Of(hall.InstanceId).IsPinned);
            Assert.IsTrue(ContainerRegistry.UnregisterRuntime(hall.RuntimeId));
            Assert.IsFalse(ScopeFrames.HasFrame(hall.InstanceId));
        }

        private static readonly Quaternion Turned = Quaternion.Euler(0f, 180f, 0f) * Quaternion.Euler(55f, 0f, 0f);

        /// <summary>The turned instance's row, as a container-ownership update brings it: its pose included.</summary>
        private Container TurnedHall()
        {
            var info = new InstanceContainerInfo
            {
                InstanceId = ScopeKeys.Hash(InstanceScope), ScopeKey = InstanceScope, PartId = "hall",
                HasPose = true, PoseOrigin = Door, PoseRotation = Turned,
            };
            // Through the wire, as a client receives it.
            var writer = new NetworkWriter();
            ContainerOwnershipMsg.Write(writer, new List<ContainerOwnershipEntry>
            {
                new ContainerOwnershipEntry { ContainerId = "rt_" + ScopeKeys.Hash(InstanceScope + "/hall"), Instance = info, HasBounds = true, BoundsSize = HallSize,
                    HasPlacement = true, Placement = ContainerPlacement.Root(Door + new Double3(0, 2, 0), HallSize) },
            });
            var reader = new NetworkReader(new ArraySegment<byte>(writer.ToArray(), 1, writer.ToArray().Length - 1));
            var received = ContainerOwnershipMsg.Read(reader).Upserts[0];
            Assert.IsTrue(received.Instance.HasPose, "the pose crosses the wire");
            Assert.Less(Quaternion.Angle(Turned, received.Instance.PoseRotation), 0.001f);
            _client.ShareInstanceFrame(received.Instance);
            return ContainerRegistry.RegisterRuntime(ScopeKeys.Hash(InstanceScope + "/hall"), received.PlacementOrRoot, received.Instance);
        }

        [Test]
        public void TheClientDrawsATurnedInstanceTurnedBehindItsDoorAndFromInsideItAndTheCameraAreTurnedNotTheWorld()
        {
            var pawn = PawnAtTheDoor(out var chunk);
            var hall = TurnedHall();
            Assert.IsTrue(InstancePoses.TryGet(hall.InstanceId, out _, out _), "the client learned the pose");
            var door = ContainerRegistry.ToFrame(Door, _space.InstanceId);
            var hallAt = hall.transform.position;
            InstanceScenes.SetView(_space.InstanceId); // the pawn stands in the world
            InstanceScenes.KeepDrawn(hall.InstanceId);
            try
            {
                // From outside: the interior is drawn turned about the door, the way the door stands.
                PhysicsFramesDraw(() =>
                {
                    var expected = door + Turned * (hallAt - door);
                    Assert.Less((hall.transform.position - expected).magnitude, 1e-3f, "the hall is drawn behind the turned door");
                    Assert.Less(Quaternion.Angle(Turned, hall.transform.rotation), 0.01f, "turned with it");
                    Assert.AreEqual(Vector3.zero, chunk.transform.position - chunk.transform.position, "the world around the door is not");
                });
                Assert.AreEqual(hallAt, hall.transform.position, "put back after drawing");
                Assert.AreEqual(Quaternion.identity, hall.transform.rotation);

                // From inside: the view is the instance, so the instance is drawn turned about its door and the camera with it; the world
                // outside is the anchor and is not moved at all (docs/portals.md D41), so its sky and sun stay upright to it.
                pawn.SetContainer(hall, reparent: false);
                InstanceScenes.SetView(hall.InstanceId);
                Assert.AreEqual(_space.InstanceId, Portals.Anchor, "the anchor is the world the pawn walked in from");
                var camera = new GameObject("camera").AddComponent<Camera>();
                _objects.Add(camera.gameObject);
                var cameraAt = door + new Vector3(0f, 0f, 5f); // 5 m along the instance's own +z from the door, in its upright space
                camera.transform.SetPositionAndRotation(cameraAt, Quaternion.identity);
                var chunkAt = chunk.transform.position;
                var cameras = new List<Camera> { camera };
                DrawnScopes.Begin(cameras, Vector3.zero);
                try
                {
                    Assert.AreEqual(chunkAt, chunk.transform.position, "the anchor's roots are never moved");
                    Assert.Less((hall.transform.position - (door + Turned * (hallAt - door))).magnitude, 1e-3f, "the instance is drawn turned about its door");
                    Assert.Less((camera.transform.position - (door + Turned * new Vector3(0f, 0f, 5f))).magnitude, 2e-3f,
                        "5 m along the instance's +z from the door is 5 m out of the door in the world's axes");
                    Assert.Less(Quaternion.Angle(Turned, camera.transform.rotation), 0.01f, "and the camera looks the way the instance does");
                }
                finally { DrawnScopes.End(); }
                Assert.AreEqual(chunkAt, chunk.transform.position, "put back after drawing");
                Assert.AreEqual(hallAt, hall.transform.position);
                Assert.AreEqual(cameraAt, camera.transform.position);
                Assert.AreEqual(Quaternion.identity, camera.transform.rotation);
            }
            finally
            {
                InstanceScenes.StopKeepingDrawn(hall.InstanceId);
                InstanceScenes.SetView(0);
            }
        }

        /// <summary>The client's drawing pass around <paramref name="check"/>, as cameras drawing run it.</summary>
        private static void PhysicsFramesDraw(Action check)
        {
            DrawnScopes.Begin(null, Vector3.zero);
            try { check(); }
            finally { DrawnScopes.End(); }
        }
    }
}
