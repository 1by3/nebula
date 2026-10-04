using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// A client outside a framed container's box sees what stands in it (<c>docs/container-tree.md</c> D23, scenario 42,
    /// NEB-386). A pilot over a planet's box sees an entity standing on the planet below, loses it when the pilot is more
    /// than <c>InterestFrameApproachMargin</c> from the box, and sees it again on the way back; the entity on the ground
    /// sees the pilot overhead the whole time (<c>AddEnclosingSpaceFoci</c>, the other direction, already). The same rule
    /// serves a station seen from a ship, a frame that is a small carried box in open space.
    /// <para>
    /// Tier B (<see cref="ConformanceMesh"/>, one worker): a real <see cref="NebulaGateway"/> in the process, fed each
    /// entity's spawn as its worker writes it (as <see cref="ConformanceFramedPlanetLapTests"/> does). A client's view
    /// is its interest set, evaluated four times a second as the pilot's pawn is moved along a path. The workers are not
    /// ticked while it flies: this is the gateway's rule, and a worker links a gateway and filters for it from the
    /// same foci and regions the gateway sends (<c>LinkNearbyOwners</c>, <c>UpdateClientRegions</c>).
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceFrameApproachTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type GatewayType = typeof(NebulaGateway);

        /// <summary>Metres from the box within which a client looks in: the default is a few kilometres, this keeps the sweeps short.</summary>
        private const float Margin = 300f;
        private const float EvalSeconds = 0.25f;
        private const float StepMetres = 10f;
        /// <summary>The pawn asks for the widest radius, 1,024 m: the entity on the ground, 600 m under the box's top, holds a pilot to this far above it.</summary>
        private const float GroundReachMetres = 400f;
        /// <summary>Once looked into, a frame is held to the margin plus a tenth of it; and an entity lingers a second (<c>InterestLingerSeconds</c>) after it is out of reach, here four evaluations of a step each.</summary>
        private const float HeldUntil = Margin + Margin * 0.1f, LingerMetres = 4 * StepMetres;
        private const ulong PilotClient = 51, GroundClient = 52;

        private static readonly Vector3 PlanetBox = new Vector3(8000f, 1000f, 8000f);
        private static readonly Vector3 PlanetBoxCenter = new Vector3(0f, 100f, 0f);
        private static readonly Vector3 PlanetAt = new Vector3(30000f, 0f, 0f);
        private const float PlanetBoxTop = 600f;
        private const float ChunkWidth = 2000f;
        private static readonly Vector3 GroundAt = new Vector3(-1030f, 1f, 0f);

        private static readonly Vector3 StationAt = new Vector3(0f, 8000f, 0f);
        private static readonly Vector3 StationBox = new Vector3(100f, 100f, 100f);
        private static readonly Vector3 CrateAt = new Vector3(40f, 0f, 0f);

        private ConformanceMesh _mesh;
        private Container _space;
        private ushort _planetPrefab, _stationPrefab, _pawnPrefab;
        private NetworkIdentity _planet, _station;
        private readonly List<Container> _chunks = new List<Container>();
        private readonly List<Vector3> _chunkCenters = new List<Vector3>();
        private readonly List<Vector3> _chunkSizes = new List<Vector3>();
        private GameObject _gatewayHost;
        private NebulaGateway _gateway;
        private ConformanceMesh.Worker W1 => _mesh[0];

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
            _mesh = new ConformanceMesh(1);
            _mesh.Config.InterestFrameApproachMargin = Margin;
            _space = _mesh.AddStaticContainer("system", Vector3.zero, new Vector3(200000f, 200000f, 200000f));
            _space.Center = Vector3.zero;
            ContainerRegistry.Rebuild();
            _mesh.SetOwner(_space, W1);

            _planetPrefab = RegisterFrame("planet-prefab", "planet", PlanetBox, PlanetBoxCenter);
            _stationPrefab = RegisterFrame("station-prefab", "station", StationBox, Vector3.zero);
            var pawn = new GameObject("pawn-prefab");
            // Asks for more than InterestMaxRadius: wide, clamped to it, so a distance is the entity's own to decide.
            pawn.AddComponent<NetworkIdentity>().RelevanceRadius = 4096f;
            _pawnPrefab = _mesh.RegisterPrefab(pawn);
        }

        private ushort RegisterFrame(string name, string id, Vector3 size, Vector3 center)
        {
            var go = new GameObject(name);
            go.AddComponent<NetworkIdentity>();
            var box = go.AddComponent<Container>();
            box.ContainerId = id;
            box.Size = size;
            box.Center = center;
            box.OwnPhysicsFrame = true;
            box.FrameInterest = FrameInterestMode.OwnRegions;
            go.AddComponent<NetworkTransform>();
            return _mesh.RegisterPrefab(go);
        }

        [TearDown]
        public void TearDown()
        {
            if (_gatewayHost != null) Object.DestroyImmediate(_gatewayHost);
            _chunks.Clear();
            _chunkCenters.Clear();
            _chunkSizes.Clear();
            _mesh.Dispose();
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
        }

        // ------------------------------------------------------------------------------------ the worlds

        /// <summary>A planet in space with four chunks of ground under it, and a standing entity on the middle ground; returns it.</summary>
        private NetworkIdentity BuildPlanet()
        {
            _planet = W1.SpawnServerDriven(_planetPrefab, _space, PlanetAt, Quaternion.identity);
            for (int i = 0; i < 4; i++) AddChunk(new Vector3(-3000f + ChunkWidth * i, PlanetBoxCenter.y, 0f), new Vector3(ChunkWidth, PlanetBox.y, PlanetBox.z));
            return SpawnIn(_pawnPrefab, _chunks[1], GroundAt);
        }

        /// <summary>A runtime chunk fixed in the planet's frame at <paramref name="center"/> (planet coordinates), leased to the worker.</summary>
        private Container AddChunk(Vector3 center, Vector3 size)
        {
            var chunk = ContainerRegistry.RegisterRuntime(2000UL + (ulong)_chunks.Count,
                ContainerPlacement.Child(_planet.Carried.ContainerId, center, size, ContainerAuthority.Leased));
            Assert.IsNotNull(chunk);
            ContainerRegistry.ApplyLease(chunk.ContainerId, W1.Id, W1.Index, 1);
            _chunks.Add(chunk);
            _chunkCenters.Add(center);
            _chunkSizes.Add(size);
            return chunk;
        }

        private NetworkIdentity SpawnIn(ushort prefab, Container container, Vector3 local)
        {
            NetworkIdentity e = null;
            W1.Act(() =>
            {
                e = NetworkPrefabs.Instantiate(prefab, local, Quaternion.identity, container.ContentRoot);
                W1.Instance.SpawnServerDriven(e, container);
            });
            if (container.InnerSpace != null && container.InnerSpace.Frame != null) e.transform.position = container.InnerSpace.Frame.LocalToSimulation(local);
            return e;
        }

        /// <summary>Everything the gateway would be told, and a pawn in space for each client that has no pawn on the ground.</summary>
        private void StartGateway()
        {
            _gatewayHost = new GameObject("gateway");
            _gateway = _gatewayHost.AddComponent<NebulaGateway>();
            GatewayType.GetField("<Config>k__BackingField", Flags).SetValue(_gateway, _mesh.Config);
            GatewayType.GetField("_transport", Flags).SetValue(_gateway, new ConformanceMesh.RecordingTransport());
            Call("InitializeInterest");
            var ownership = (IList)Field("_ownership");
            var byId = (IDictionary)Field("_ownershipById");
            var frames = (HashSet<string>)Field("_ownRegionFrames");
            var carriers = (HashSet<ulong>)Field("_ownRegionCarriers");
            foreach (var frame in new[] { _planet, _station })
            {
                if (frame == null) continue;
                var row = new LeaseInfo
                {
                    ContainerId = frame.Carried.ContainerId, WorkerId = W1.Id, Epoch = 1, State = LeaseState.Active,
                    OwnPhysicsFrame = true, FrameInterest = FrameInterestMode.OwnRegions,
                };
                var entry = ContainerOwnershipEntry.Of(row, ContainerRef.DynamicIndex, W1.Index);
                ownership.Add(entry);
                byId[row.ContainerId] = entry;
                frames.Add(row.ContainerId);
                carriers.Add(frame.NetId);
            }
            for (int i = 0; i < _chunks.Count; i++)
            {
                var chunk = _chunks[i];
                var row = new LeaseInfo
                {
                    ContainerId = chunk.ContainerId, WorkerId = W1.Id, Epoch = 1, State = LeaseState.Active,
                    HasBounds = true, ParentId = _planet.Carried.ContainerId,
                    Center = Double3.From(_chunkCenters[i]), BoundsSize = _chunkSizes[i], Authority = ContainerAuthority.Leased,
                };
                var entry = ContainerOwnershipEntry.Of(row, ContainerRef.RuntimeIndex, W1.Index);
                ownership.Add(entry);
                byId[row.ContainerId] = entry;
            }
        }

        /// <summary>
        /// A standalone gateway holds no entities, so its registry has no box for a carried planet: only its leases do.
        /// The registry of this process does (the worker's), so it is taken out once everything has been announced (taking
        /// it out evacuates what stands in it on the worker, which is not what the gateway was told).
        /// </summary>
        private void ForgetPlanetBox() => ContainerRegistry.UnregisterDynamic(_planet.Carried);

        // ------------------------------------------------------------------------------------ the gateway

        private object Field(string name) => GatewayType.GetField(name, Flags).GetValue(_gateway);

        private object Call(string name, params object[] args)
        {
            try { return GatewayType.GetMethod(name, Flags).Invoke(_gateway, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }

        private static T Of<T>(object o, string field) => (T)o.GetType().GetField(field).GetValue(o);

        private object Client(ulong clientId, int peerId)
        {
            var type = GatewayType.GetNestedType("ClientConn", BindingFlags.NonPublic);
            var client = Activator.CreateInstance(type, true);
            type.GetField("ClientId").SetValue(client, clientId);
            type.GetField("PeerId").SetValue(client, peerId);
            type.GetField("Welcomed").SetValue(client, true);
            ((IDictionary)Field("_clientsById")).Add(clientId, client);
            return client;
        }

        private object WorkerConn()
        {
            var type = GatewayType.GetNestedType("WorkerConn", BindingFlags.NonPublic);
            var conn = Activator.CreateInstance(type, true);
            type.GetField("WorkerId").SetValue(conn, W1.Id);
            type.GetField("Index").SetValue(conn, W1.Index);
            type.GetField("Ready").SetValue(conn, true);
            return conn;
        }

        private void Announce(NetworkIdentity entity, ulong ownerClientId = 0)
        {
            EntitySpawnMsg msg = default;
            W1.Act(() => msg = EntitySpawnMsg.From(entity, new NetworkWriter(256), forGateway: true));
            msg.OwnerClientId = ownerClientId;
            Call("OnEntitySpawn", WorkerConn(), msg);
        }

        private bool Sees(object client, NetworkIdentity entity) => Of<HashSet<ulong>>(client, "Visible").Contains(entity.NetId);

        // ------------------------------------------------------------------------------------ the sweeps

        /// <summary>What a client held of one entity along a sweep, by the pilot's distance from the box.</summary>
        private sealed class Sweep
        {
            public readonly StringBuilder Log = new StringBuilder();
            public bool SawAtStart;
            public float LostAt = float.NaN, RegainedAt = float.NaN, PeakDistance;
            public int GroundMissedPilot;
            public int Samples;
            public bool EverSaw;
        }

        /// <summary>
        /// Fly the pilot from <paramref name="from"/> to <paramref name="to"/> and back in steps of ten metres, four
        /// evaluations a second, along <paramref name="axis"/>. <paramref name="distanceToBox"/> names where each
        /// sample is (metres from the box), <paramref name="seen"/> is the entity looked at from the pilot's client.
        /// </summary>
        private Sweep Fly(NetworkIdentity pilot, object pilotClient, NetworkIdentity seen, Func<float, Vector3> worldAt, float distanceFrom, float distanceTo,
            object groundClient = null)
        {
            var sweep = new Sweep();
            int steps = Mathf.RoundToInt((distanceTo - distanceFrom) / StepMetres);
            double now = 100.0;
            bool held = false;
            for (int leg = 0; leg < 2; leg++)
            {
                for (int i = 0; i <= steps; i++)
                {
                    float d = distanceFrom + (leg == 0 ? i : steps - i) * StepMetres;
                    pilot.transform.position = worldAt(d);
                    Announce(pilot, PilotClient);
                    now += EvalSeconds;
                    Call("EvaluateClient", pilotClient, now);
                    if (groundClient != null) Call("EvaluateClient", groundClient, now);
                    bool sees = Sees(pilotClient, seen);
                    sweep.Samples++;
                    sweep.EverSaw |= sees;
                    if (groundClient != null && d <= GroundReachMetres && !Sees(groundClient, pilot)) sweep.GroundMissedPilot++;
                    if (sweep.Samples == 1) sweep.SawAtStart = sees;
                    if (sees != held)
                    {
                        sweep.Log.AppendLine($"{(leg == 0 ? "out" : "back")} at {d:0} m from the box: {(sees ? "gained" : "lost")}");
                        if (!sees && float.IsNaN(sweep.LostAt)) sweep.LostAt = d;
                        if (sees && !float.IsNaN(sweep.LostAt) && float.IsNaN(sweep.RegainedAt)) sweep.RegainedAt = d;
                        held = sees;
                    }
                    sweep.PeakDistance = Mathf.Max(sweep.PeakDistance, d);
                }
            }
            TestContext.WriteLine(sweep.Log.ToString());
            foreach (DictionaryEntry kv in (IDictionary)Field("_frameExtents")) TestContext.WriteLine("frame box " + kv.Value);
            return sweep;
        }

        // ------------------------------------------------------------------------------------ scenario 42

        /// <summary>
        /// A pilot over the planet's box, 50 m above its top and then climbing to 450 m above it and back, looks at a
        /// standing entity 600 m below the top (it asks for the widest radius, 1,024 m). It sees it from the start, loses
        /// it when it is more than the margin (300 m) plus the exit margin from the box, after the entity's linger, and
        /// sees it again within the margin on the way down. The pilot's own pawn is seen by the client on the ground at
        /// every step while it is within the pawn's radius of it (to 400 m above the box's top).
        /// </summary>
        [Test]
        public void APilotAboveAPlanetSeesWhatStandsOnItWithinTheMargin()
        {
            var ground = BuildPlanet();
            var pilot = SpawnIn(_pawnPrefab, _space, PlanetAt + new Vector3(GroundAt.x, PlanetBoxTop + 50f, 0f));
            pilot.transform.position = PlanetAt + new Vector3(GroundAt.x, PlanetBoxTop + 50f, 0f);
            W1.Tick(1); _mesh.Pump();
            StartGateway();
            Announce(_planet);
            var pilotClient = Client(PilotClient, 7);
            var groundClient = Client(GroundClient, 8);
            Announce(ground, GroundClient);

            var sweep = Fly(pilot, pilotClient, ground, d => PlanetAt + new Vector3(GroundAt.x, PlanetBoxTop + d, 0f), 50f, 450f, groundClient);

            Assert.IsTrue(sweep.SawAtStart, "within the margin of the box at the start: the entity on the ground below is in the pilot's set");
            Assert.That(sweep.LostAt, Is.GreaterThan(HeldUntil).And.LessThan(HeldUntil + LingerMetres + 2 * StepMetres),
                "lost once the pilot is beyond the margin plus the exit margin, after the entity's linger: not at the box's top and not at the entity's own distance");
            Assert.That(sweep.RegainedAt, Is.GreaterThan(0f).And.LessThanOrEqualTo(Margin), "and seen again as soon as the pilot is back within the margin");
            Assert.AreEqual(0, sweep.GroundMissedPilot, "the client on the ground sees the pilot overhead at every step");
        }

        /// <summary>
        /// At the default margin (4 km) the pilot sees the ground from kilometres up, though the entity's radius reaches
        /// 1,024 m: the focus is on the box's face, not at the pilot's own place, and the box's face is 600 m above the
        /// entity. It stops at the margin plus a tenth of it.
        /// </summary>
        [Test]
        public void AtTheDefaultMarginAPilotSeesTheGroundFromKilometresUp()
        {
            _mesh.Config.InterestFrameApproachMargin = new NebulaConfig().InterestFrameApproachMargin;
            Assert.AreEqual(4000f, _mesh.Config.InterestFrameApproachMargin, "the default is a few kilometres");
            var ground = BuildPlanet();
            var pilot = SpawnIn(_pawnPrefab, _space, PlanetAt + new Vector3(GroundAt.x, PlanetBoxTop + 2000f, 0f));
            W1.Tick(1); _mesh.Pump();
            StartGateway();
            Announce(_planet);
            var pilotClient = Client(PilotClient, 7);
            Announce(ground, GroundClient);
            var sweep = Fly(pilot, pilotClient, ground, d => PlanetAt + new Vector3(GroundAt.x, PlanetBoxTop + d, 0f), 2000f, 4800f);
            float held = 4000f * 1.1f;
            Assert.IsTrue(sweep.SawAtStart, "2 km above the box's top, 2.6 km from the entity: in the pilot's set");
            Assert.That(sweep.LostAt, Is.GreaterThan(held).And.LessThan(held + LingerMetres + 2 * StepMetres), "lost past the margin plus a tenth, after the linger");
            Assert.That(sweep.RegainedAt, Is.GreaterThan(0f).And.LessThanOrEqualTo(4000f), "and seen again within the margin");
        }

        /// <summary>The margin is what does it: with <c>InterestFrameApproachMargin</c> 0 the pilot over the box never sees the ground.</summary>
        [Test]
        public void WithTheMarginOffAPilotAboveAPlanetNeverSeesTheGround()
        {
            _mesh.Config.InterestFrameApproachMargin = 0f;
            var ground = BuildPlanet();
            var pilot = SpawnIn(_pawnPrefab, _space, PlanetAt + new Vector3(GroundAt.x, PlanetBoxTop + 50f, 0f));
            W1.Tick(1); _mesh.Pump();
            StartGateway();
            Announce(_planet);
            var pilotClient = Client(PilotClient, 7);
            var groundClient = Client(GroundClient, 8);
            Announce(ground, GroundClient);
            var sweep = Fly(pilot, pilotClient, ground, d => PlanetAt + new Vector3(GroundAt.x, PlanetBoxTop + d, 0f), 50f, 450f, groundClient);
            Assert.IsFalse(sweep.EverSaw, "no focus in the planet's frame: the ground below is not in the set");
            Assert.AreEqual(0, sweep.GroundMissedPilot, "the other direction needs no margin");
        }

        /// <summary>
        /// A gateway of its own holds no box for a carried planet, only the leases of the chunks fixed in it: the box is
        /// then the union of theirs, in the planet's own coordinates.
        /// </summary>
        [Test]
        public void AGatewayWithNoBoxForAPlanetMeasuresItFromItsChunkLeases()
        {
            BuildPlanet();
            W1.Tick(1); _mesh.Pump();
            StartGateway();
            Announce(_planet);
            ForgetPlanetBox();
            var reference = ContainerRef.Dynamic(_planet.NetId);
            Assert.IsNull(ContainerRegistry.Resolve(reference), "the registry no longer knows the planet's box");
            var args = new object[] { reference, RegionKeys.FrameKeyOf(reference), null, null };
            Call("FrameExtent", args);
            Assert.AreEqual(new Vector3(-4000f, -400f, -4000f), (Vector3)args[2], "the four chunks' boxes, side by side");
            Assert.AreEqual(new Vector3(4000f, 600f, 4000f), (Vector3)args[3]);
        }

        // ------------------------------------------------------------------------------------ the ground's rows (NEB-396)

        /// <summary>A row of small chunks across the planet, 512 m wide: the planet's own ground, streamed as a hosted grid would lease it.</summary>
        private const float SmallChunk = 512f;
        private const int SmallChunks = 16;

        private void BuildGroundRow()
        {
            _planet = W1.SpawnServerDriven(_planetPrefab, _space, PlanetAt, Quaternion.identity);
            for (int i = 0; i < SmallChunks; i++)
                AddChunk(new Vector3(-4096f + SmallChunk * (i + 0.5f), PlanetBoxCenter.y, 0f), new Vector3(SmallChunk, PlanetBox.y, SmallChunk));
        }

        private static int ChunkAt(float x) => Mathf.FloorToInt((x + 4096f) / SmallChunk);

        private static HashSet<string> Rows(object client) => Of<HashSet<string>>(client, "KnownContainers");

        private static void SetPawn(object client, NetworkIdentity pawn) =>
            GatewayType.GetNestedType("ClientConn", BindingFlags.NonPublic).GetField("PawnNetId").SetValue(client, pawn.NetId);

        /// <summary>
        /// A client is told the rows of the chunks it needs to build round it. Chunks fixed in a planet's frame are
        /// boxes in the planet's coordinates, which a window in the scope's own space never overlaps, so a pawn standing
        /// on the planet was told only of the chunk it stood in (NEB-396). It is now told of the chunks round it in the
        /// planet's frame, and still not of those far away.
        /// </summary>
        [Test]
        public void APawnOnAPlanetIsToldTheRowsOfTheGroundRoundIt()
        {
            BuildGroundRow();
            var at = new Vector3(500f, 1f, 0f); // 12 m from the next chunk's edge
            var ground = SpawnIn(_pawnPrefab, _chunks[ChunkAt(at.x)], at);
            W1.Tick(1); _mesh.Pump();
            StartGateway();
            Announce(_planet);
            var client = Client(GroundClient, 8);
            SetPawn(client, ground);
            Announce(ground, GroundClient);
            Call("EvaluateClient", client, 100.0);

            var rows = Rows(client);
            Assert.That(rows, Does.Contain(_chunks[ChunkAt(at.x)].ContainerId), "the chunk it stands in");
            Assert.That(rows, Does.Contain(_chunks[ChunkAt(at.x) + 1].ContainerId), "and the chunk 12 m away, in the planet's frame");
            Assert.That(rows, Does.Not.Contain(_chunks[ChunkAt(3800f)].ContainerId), "not the far ground, 3 km away");
        }

        /// <summary>
        /// A pilot above the planet's box looks into it through its approach focus (D23), on the box's face below it: it is
        /// told the rows of the ground round that focus, in the planet's frame, so what it sees standing there has ground
        /// to stand on; and not of ground far from it.
        /// </summary>
        [Test]
        public void APilotAboveAPlanetIsToldTheRowsOfTheGroundUnderItsFocus()
        {
            BuildGroundRow();
            var above = PlanetAt + new Vector3(500f, PlanetBoxTop + 50f, 0f);
            var pilot = SpawnIn(_pawnPrefab, _space, above);
            pilot.transform.position = above;
            W1.Tick(1); _mesh.Pump();
            StartGateway();
            Announce(_planet);
            var client = Client(PilotClient, 7);
            SetPawn(client, pilot);
            Announce(pilot, PilotClient);
            Call("EvaluateClient", client, 100.0);

            var rows = Rows(client);
            Assert.That(rows, Does.Contain(_chunks[ChunkAt(500f)].ContainerId), "the ground under the pilot's focus on the box's top");
            Assert.That(rows, Does.Contain(_chunks[ChunkAt(500f) + 1].ContainerId), "and beside it");
            Assert.That(rows, Does.Not.Contain(_chunks[ChunkAt(3800f)].ContainerId), "not ground far from it");
        }

        /// <summary>
        /// The same rule where the frame is no planet: a station is a small carried box in open space, a crate stands in
        /// it 10 m inside its face, and a ship flies away from it and back. The ship sees the crate within the margin of
        /// the box and not beyond.
        /// </summary>
        [Test]
        public void AShipSeesWhatStandsInAStationWithinTheMargin()
        {
            _station = W1.SpawnServerDriven(_stationPrefab, _space, StationAt, Quaternion.identity);
            var crate = SpawnIn(_pawnPrefab, _station.Carried, CrateAt);
            var ship = SpawnIn(_pawnPrefab, _space, StationAt + new Vector3(StationBox.x / 2 + 50f, 0f, 0f));
            W1.Tick(1); _mesh.Pump();
            StartGateway();
            Announce(_station);
            Announce(crate);
            var shipClient = Client(PilotClient, 7);

            float face = StationBox.x / 2f;
            var sweep = Fly(ship, shipClient, crate, d => StationAt + new Vector3(face + d, 0f, 0f), 50f, 450f);

            Assert.IsTrue(sweep.SawAtStart, "50 m from the station's face: the crate in it is in the ship's set");
            Assert.That(sweep.LostAt, Is.GreaterThan(HeldUntil).And.LessThan(HeldUntil + LingerMetres + 2 * StepMetres), "lost beyond the margin plus the exit margin, after the entity's linger");
            Assert.That(sweep.RegainedAt, Is.GreaterThan(0f).And.LessThanOrEqualTo(Margin), "and seen again within the margin on the way back");
        }
    }
}
