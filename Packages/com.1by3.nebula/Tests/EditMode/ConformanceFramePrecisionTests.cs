using System;
using System.Collections;
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
    /// Conformance scenario 45 (<c>docs/scope-frames.md</c> section 7, <c>docs/container-tree.md</c> D24, NEB-387): what
    /// a position on the wire is worth inside a framed container whose box is large, and whose scope sits far from the
    /// origin. The decision pinned here is that positions stay container-local float32: an entity in a chunk the planet
    /// hosts is near-exact however big the planet is, an entity directly in the planet's frame is good to the float
    /// spacing at its distance from the frame origin (1.6 cm at 250 km), and neither depends on where the planet's scope
    /// is placed (here a root 10,000 km from the origin, the place scenario 22 pins for placement).
    /// <para>
    /// Tier B (<see cref="ConformanceMesh"/>, one worker, a real <see cref="NebulaGateway"/> in process fed the spawns as
    /// the worker writes them). The spawn is written and read back through <see cref="EntitySpawnMsg"/>, the message
    /// every replica and every gateway record starts from.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceFramePrecisionTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type GatewayType = typeof(NebulaGateway);

        /// <summary>10,000 km out, with a fraction a float could not hold at that distance (as scenario 22).</summary>
        private const double RootX = 10_000_000.0 + 12.3456;
        /// <summary>A planet of 205 km radius with headroom: the frame's box is 500 km on a side, 250 km to each face.</summary>
        private const float PlanetSize = 500_000f;
        private const float ChunkWidth = 512f;
        /// <summary>The spacing of a float between 2^17 and 2^18 metres (131 km and 262 km) is 2^-6 m; between 2^16 and 2^17 it is 2^-7 m.</summary>
        private const double SpacingAt250Km = 1.0 / 64.0, SpacingAt100Km = 1.0 / 128.0;

        private ConformanceMesh _mesh;
        private WorldDefinition _definition;
        private Container _scope;
        private ushort _planetPrefab, _thingPrefab;
        private NetworkIdentity _planet;
        private GameObject _gatewayHost;
        private NebulaGateway _gateway;
        private ConformanceMesh.Worker W1 => _mesh[0];

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
            _definition = ScriptableObject.CreateInstance<WorldDefinition>();
            _definition.CellSize = Vector3.one * 1000f;
            NebulaWorld.LoadRuntime(_definition);
            _mesh = new ConformanceMesh(1);
            NebulaWorld.Streamer.ShiftOrigin(new Vector3Int(10_000, 0, 0)); // this process's origin is out there
            // The scope root is a small box: a static box is hashed by the registry bucket by bucket, and the planet (a carried box,
            // which is not) is what is large here.
            _scope = ContainerRegistry.RegisterRuntime(900, ContainerPlacement.Root(new Double3(RootX, 0.0, 0.0), new Vector3(200f, 200f, 200f)));
            Assert.IsNotNull(_scope);
            ContainerRegistry.ApplyLease(_scope.ContainerId, W1.Id, W1.Index, 1);

            var planet = new GameObject("planet-prefab");
            planet.AddComponent<NetworkIdentity>();
            var box = planet.AddComponent<Container>();
            box.ContainerId = "planet";
            box.Size = Vector3.one * PlanetSize;
            box.Center = Vector3.zero;
            box.OwnPhysicsFrame = true;
            box.FrameInterest = FrameInterestMode.OwnRegions;
            planet.AddComponent<NetworkTransform>(); // with the Container on its root, the entity carries the box
            _planetPrefab = _mesh.RegisterPrefab(planet);
            var thing = new GameObject("thing-prefab");
            thing.AddComponent<NetworkIdentity>();
            _thingPrefab = _mesh.RegisterPrefab(thing);

            _planet = W1.SpawnServerDriven(_planetPrefab, _scope, _scope.transform.position, Quaternion.identity);
        }

        [TearDown]
        public void TearDown()
        {
            if (_gatewayHost != null) Object.DestroyImmediate(_gatewayHost);
            _mesh.Dispose();
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
            foreach (var c in new List<Container>(ContainerRegistry.Runtime)) if (c != null) Object.DestroyImmediate(c.gameObject);
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.Rebuild();
            NebulaWorld.Unload();
            Object.DestroyImmediate(_definition);
        }

        private NetworkIdentity SpawnIn(Container container, Vector3 local)
        {
            NetworkIdentity e = null;
            W1.Act(() =>
            {
                e = NetworkPrefabs.Instantiate(_thingPrefab, local, Quaternion.identity, container.ContentRoot);
                W1.Instance.SpawnServerDriven(e, container);
            });
            if (container.InnerSpace != null && container.InnerSpace.Frame != null) e.transform.position = container.InnerSpace.Frame.LocalToSimulation(local);
            return e;
        }

        /// <summary>
        /// An entity standing in a chunk at <paramref name="local"/> (chunk coordinates). A frame's floating origin follows
        /// what its worker simulates (D19), so what stands in a chunk is simulated near the frame's origin: its simulation
        /// position is its chunk-local one.
        /// </summary>
        private NetworkIdentity SpawnInChunk(Container chunk, Vector3 local)
        {
            NetworkIdentity e = null;
            W1.Act(() =>
            {
                e = NetworkPrefabs.Instantiate(_thingPrefab, Vector3.zero, Quaternion.identity, chunk.ContentRoot);
                e.transform.localPosition = local;
                W1.Instance.SpawnServerDriven(e, chunk);
            });
            return e;
        }

        /// <summary>The spawn as a gateway receives it: written by the worker, read back from the bytes.</summary>
        private EntitySpawnMsg OnTheWire(NetworkIdentity entity)
        {
            EntitySpawnMsg sent = default;
            W1.Act(() => sent = EntitySpawnMsg.From(entity, new NetworkWriter(256), forGateway: true));
            var writer = new NetworkWriter(512);
            sent.Write(writer, MsgId.EntitySpawn);
            var reader = new NetworkReader(writer.ToSegment());
            Assert.AreEqual((byte)MsgId.EntitySpawn, reader.ReadByte());
            return EntitySpawnMsg.Read(reader);
        }

        private Container AddChunk(ulong runtimeId, Vector3 center)
        {
            var chunk = ContainerRegistry.RegisterRuntime(runtimeId,
                ContainerPlacement.Child(_planet.Carried.ContainerId, center, new Vector3(ChunkWidth, ChunkWidth, ChunkWidth), ContainerAuthority.Leased));
            Assert.IsNotNull(chunk);
            ContainerRegistry.ApplyLease(chunk.ContainerId, W1.Id, W1.Index, 1);
            return chunk;
        }

        // ------------------------------------------------------------------------------------ the worker's wire

        [Test]
        public void AnEntityStandingInAHostedChunkIsNearExactWhereverTheChunkIs()
        {
            // The chunk is 100 km, then 240 km, from the planet's centre in the planet's own coordinates.
            var chunkCenters = new[] { new Vector3(100_000f, 0f, 0f), new Vector3(0f, 0f, -240_000f) };
            for (int i = 0; i < chunkCenters.Length; i++)
            {
                var chunk = AddChunk(1000UL + (ulong)i, chunkCenters[i]);
                var local = new Vector3(3.14159f, 1.25f, -7.5f);
                var entity = SpawnInChunk(chunk, local);
                var msg = OnTheWire(entity);
                Assert.AreEqual(ContainerRef.Of(chunk), msg.Container, "it names its chunk, not the planet");
                float error = Vector3.Distance(local, msg.LocalPosition);
                TestContext.WriteLine($"chunk at {chunkCenters[i]}: wire error {error:E3} m");
                Assert.Less(error, 1e-4f, "container-local numbers stay small however far the chunk is from the frame's origin");
            }
        }

        [Test]
        public void AnEntityDirectlyInTheFrameIsGoodToTheFloatSpacingAtItsDistance()
        {
            // 100 km and 250 km from the frame's origin, with fractions a float must round.
            var cases = new[]
            {
                new Vector3(100_000.123f, 5.5f, -80_000.77f),
                new Vector3(249_999.37f, 10.25f, -249_999.91f),
            };
            var truth = new[]
            {
                new Double3(100_000.123, 5.5, -80_000.77),
                new Double3(249_999.37, 10.25, -249_999.91),
            };
            double worst = 0;
            for (int i = 0; i < cases.Length; i++)
            {
                var entity = SpawnIn(_planet.Carried, cases[i]);
                var msg = OnTheWire(entity);
                Assert.AreEqual(ContainerRef.Of(_planet.Carried), msg.Container);
                double spacing = i == 0 ? SpacingAt100Km : SpacingAt250Km;
                double ex = Math.Abs(msg.LocalPosition.x - truth[i].X), ez = Math.Abs(msg.LocalPosition.z - truth[i].Z);
                double ey = Math.Abs(msg.LocalPosition.y - truth[i].Y);
                TestContext.WriteLine($"directly in the frame at {cases[i]}: wire error x {ex:E3} y {ey:E3} z {ez:E3} m (spacing {spacing:0.0000} m)");
                worst = Math.Max(worst, Math.Max(ex, ez));
                Assert.LessOrEqual(ex, spacing / 2 + 1e-9, "x is rounded to the nearest float, at most half a spacing away");
                Assert.LessOrEqual(ez, spacing / 2 + 1e-9, "z likewise");
                Assert.LessOrEqual(ey, 1e-5, "y, near the frame's origin plane, keeps its fraction");
            }
            Assert.LessOrEqual(worst, 0.0157, "1.6 cm, the float spacing between 131 km and 262 km, is the worst a hull in this planet's frame sees");
        }

        [Test]
        public void WhatTheFrameHoldsDoesNotDependOnWhereTheScopeIs()
        {
            // The wire position is the container-local float, exactly, though the scope's root is 10,000 km out.
            var local = new Vector3(123_456.789f, 42.5f, -98_765.432f);
            var wire = OnTheWire(SpawnIn(_planet.Carried, local));
            Assert.AreEqual(local, wire.LocalPosition, "the wire position is the container-local float, exactly");
            var back = ContainerRegistry.ToAbsolutePrecise(_scope.transform.position, 0);
            Assert.That(back.X, Is.EqualTo(RootX).Within(0.01), "and the scope is where the root says it is, in double");
        }

        // ------------------------------------------------------------------------------------ the gateway's record

        private object Field(string name) => GatewayType.GetField(name, Flags).GetValue(_gateway);

        private object Call(string name, params object[] args)
        {
            try { return GatewayType.GetMethod(name, Flags).Invoke(_gateway, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }

        private void StartGateway(params Container[] chunks)
        {
            _gatewayHost = new GameObject("gateway");
            _gateway = _gatewayHost.AddComponent<NebulaGateway>();
            GatewayType.GetField("<Config>k__BackingField", Flags).SetValue(_gateway, _mesh.Config);
            GatewayType.GetField("_transport", Flags).SetValue(_gateway, new ConformanceMesh.RecordingTransport());
            Call("InitializeInterest");
            var ownership = (IList)Field("_ownership");
            var byId = (IDictionary)Field("_ownershipById");
            var row = new LeaseInfo
            {
                ContainerId = _planet.Carried.ContainerId, WorkerId = W1.Id, Epoch = 1, State = LeaseState.Active,
                OwnPhysicsFrame = true, FrameInterest = FrameInterestMode.OwnRegions,
            };
            var entry = ContainerOwnershipEntry.Of(row, ContainerRef.DynamicIndex, W1.Index);
            ownership.Add(entry);
            byId[row.ContainerId] = entry;
            ((HashSet<string>)Field("_ownRegionFrames")).Add(row.ContainerId);
            ((HashSet<ulong>)Field("_ownRegionCarriers")).Add(_planet.NetId);
            foreach (var chunk in chunks)
            {
                var c = new LeaseInfo
                {
                    ContainerId = chunk.ContainerId, WorkerId = W1.Id, Epoch = 1, State = LeaseState.Active,
                    HasBounds = true, ParentId = _planet.Carried.ContainerId,
                    Center = Double3.From(chunk.transform.localPosition), BoundsSize = chunk.Size, Authority = ContainerAuthority.Leased,
                };
                var e = ContainerOwnershipEntry.Of(c, ContainerRef.RuntimeIndex, W1.Index);
                ownership.Add(e);
                byId[c.ContainerId] = e;
            }
        }

        private void Announce(NetworkIdentity entity)
        {
            EntitySpawnMsg msg = default;
            W1.Act(() => msg = EntitySpawnMsg.From(entity, new NetworkWriter(256), forGateway: true));
            var type = GatewayType.GetNestedType("WorkerConn", BindingFlags.NonPublic);
            var conn = Activator.CreateInstance(type, true);
            type.GetField("WorkerId").SetValue(conn, W1.Id);
            type.GetField("Index").SetValue(conn, W1.Index);
            type.GetField("Ready").SetValue(conn, true);
            Call("OnEntitySpawn", conn, msg);
        }

        private (double x, double y, double z, ulong frameKey) RecordOf(NetworkIdentity entity)
        {
            object rec = ((IDictionary)Field("_entities"))[entity.NetId];
            Assert.IsNotNull(rec, "the gateway holds the entity");
            var t = rec.GetType();
            return ((double)t.GetField("AbsX").GetValue(rec), (double)t.GetField("AbsY").GetValue(rec), (double)t.GetField("AbsZ").GetValue(rec), (ulong)t.GetField("FrameKey").GetValue(rec));
        }

        [Test]
        public void TheGatewaysAbsoluteRecordsAreDoubleFields()
        {
            var rec = GatewayType.GetNestedType("EntityRecord", BindingFlags.NonPublic);
            foreach (var name in new[] { "AbsX", "AbsY", "AbsZ" })
                Assert.AreEqual(typeof(double), rec.GetField(name).FieldType, name);
        }

        [Test]
        public void TheGatewayFilesAnEntityOfAFramedPlanetInTheFramesOwnSpaceNotAtTenThousandKilometres()
        {
            var chunk = AddChunk(1000UL, new Vector3(100_000f, 0f, 0f));
            var inChunk = SpawnInChunk(chunk, new Vector3(3.14159f, 1.25f, -7.5f));
            var inFrame = SpawnIn(_planet.Carried, new Vector3(249_999.37f, 10.25f, -249_999.91f));
            StartGateway(chunk);
            Announce(_planet);
            Announce(inChunk);
            Announce(inFrame);

            var c = RecordOf(inChunk);
            var f = RecordOf(inFrame);
            TestContext.WriteLine($"gateway record, in a hosted chunk: ({c.x:0.#####}, {c.y:0.#####}, {c.z:0.#####}) key {c.frameKey}");
            TestContext.WriteLine($"gateway record, directly in the frame: ({f.x:0.#####}, {f.y:0.#####}, {f.z:0.#####}) key {f.frameKey}");
            Assert.AreNotEqual(0UL, c.frameKey, "a chunk of a planet with regions of its own is filed in the planet's region space");
            Assert.AreNotEqual(0UL, f.frameKey);
            Assert.AreEqual(f.frameKey, c.frameKey);
            Assert.That(c.x, Is.EqualTo(100_000.0 + 3.14159).Within(SpacingAt100Km / 2 + 1e-6), "frame-local, the chunk's centre plus the entity's place in it");
            Assert.That(c.y, Is.EqualTo(1.25).Within(1e-4));
            Assert.That(c.z, Is.EqualTo(-7.5).Within(1e-4));
            Assert.That(f.x, Is.EqualTo(249_999.37).Within(SpacingAt250Km / 2 + 1e-6), "frame-local, nowhere near 10,000 km");
            Assert.That(f.z, Is.EqualTo(-249_999.91).Within(SpacingAt250Km / 2 + 1e-6));
        }

        [Test]
        public void TheGatewayRecordOfAnEntityInTheScopeItselfIsOnlyFloatPrecise()
        {
            // The record's fields are doubles, but the position they are filled from is composed in float
            // (RegionSpaceOf and AbsoluteOf return Vector3). For an entity that stands in the scope itself, not in a
            // frame with regions of its own, 10,000 km out, that is a float's resolution there: about a metre. Pinned
            // as the current limit, not as a goal: interest is bucketed in tens to hundreds of metres, so it decides
            // nothing (docs/scope-frames.md section 7).
            var ship = SpawnIn(_scope, new Vector3(250.37f, 75.5f, -125.25f)); // world coordinates near this process's origin
            StartGateway();
            Announce(ship);
            var r = RecordOf(ship);
            double truthX = 10_000_000.0 + 250.37; // the process origin is at exactly 10,000 km
            double error = Math.Abs(r.x - truthX);
            TestContext.WriteLine($"gateway record, in the scope at 10,000 km: x {r.x:R} vs {truthX:R}, error {error:0.000} m, key {r.frameKey}");
            Assert.AreEqual(0UL, r.frameKey);
            Assert.LessOrEqual(error, 0.5, "a float at 10,000 km has a spacing of 1 m: rounded to the nearest metre");
        }
    }
}
