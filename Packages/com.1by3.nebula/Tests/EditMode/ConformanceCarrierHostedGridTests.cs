using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Nebula.World;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 41 (<c>docs/conformance-suite.md</c>, design <c>docs/container-tree.md</c> D22): a planet's
    /// ground streamed around players as a chunk grid hosted by the planet. The planet is a carrier with a physics frame
    /// and regions of its own in one scope; nothing is leased statically. Each worker's
    /// <see cref="RuntimeGridAllocator"/> leases the planet's chunks, as children of the planet's container laid out in
    /// its own coordinates, around the pawns it simulates on or above the planet, with a lead along each pawn's velocity.
    /// Scenario 40's lap is flown over it: a ship with a crew member aboard takes off beside one ground pawn, crosses into
    /// the chunks leased around a second pawn on the other worker at 300 m/s, climbs out of the planet's box to 15 km and
    /// comes straight back down onto ground that retired while it was away. And a restart brings back an entity standing
    /// in a hosted chunk, under the planet's own
    /// restored container.
    /// <para>
    /// Tier B (<see cref="ConformanceMesh"/>) with a <see cref="LocalControlPlane"/>: the allocators write real lease rows,
    /// and every worker's registry (its own carried boxes; the static and runtime containers are shared) is mirrored from the
    /// rows each tick as a worker's control-plane pass mirrors them. The planet's grid is shared by both workers.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceCarrierHostedGridTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private const float Dt = NetworkTime.TickInterval;
        private const string Scope = "space/conformance";
        private const string GridKey = "planet/conformance/0";
        private const ulong SpaceId = 777UL;

        /// <summary>The planet's box: 8 km square, 600 m above and below its ground at y = 0.</summary>
        private static readonly Vector3 PlanetBox = new Vector3(8192f, 1200f, 8192f);
        private static readonly Vector3 PlanetAt = new Vector3(30000f, 0f, 0f);
        private const float TopSpeed = 300f;
        private const float Ceiling = 15000f;
        private const float LeadSeconds = 2f;
        private const float Reach = 3000f;
        private const float RetireSeconds = 3f;
        private const ulong GroundClient = 41, FarClient = 42, CrewClient = 43;

        private static ChunkGridDefinition Ground => new ChunkGridDefinition
        {
            CellSize = new Vector3(512f, PlanetBox.y, 512f),
            Planar = true,
            Ring = 1,
            RetireSeconds = RetireSeconds,
            LeadSeconds = LeadSeconds,
            Reach = Reach,
        };

        /// <summary>
        /// The lap, planet-local: take off in w1's ground, cross into the ground leased around the far pawn on w2 300 m
        /// up, climb out of the box to 15 km and come straight back down, a kilometre from where it took off, onto ground
        /// that retired while it was away.
        /// </summary>
        private static readonly Vector3[] Waypoints =
        {
            new Vector3(-1000f, 0f, Lane),
            new Vector3(3000f, 300f, Lane),
            new Vector3(256f, Ceiling, Lane),
            new Vector3(256f, 0f, Lane),
        };
        /// <summary>The lap runs along z = 200 m, down the middle of a row of 512 m chunks rather than along a seam.</summary>
        private const float Lane = 200f;
        private static readonly Vector3 GroundPawnAt = new Vector3(-1100f, 1f, Lane);
        private static readonly Vector3 FarPawnAt = new Vector3(2600f, 1f, Lane);
        private static readonly Vector3 CrewSeat = new Vector3(0f, 1f, 6f);
        private static readonly Vector3 CrateAt = new Vector3(700f, 1f, 300f);

        private ConformanceMesh _mesh;
        private LocalControlPlane _plane;
        private LocalPersistenceStore _store;
        private NebulaPersistence _persistence;
        private DateTime _clock;
        private float _seconds;
        private ushort _planetPrefab, _shipPrefab, _pawnPrefab, _cratePrefab;

        private ConformanceMesh.Worker W1 => _mesh[0];

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
            _clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            _seconds = 0f;
            _plane = new LocalControlPlane { Clock = () => _clock };
            _plane.Connect();
            // The scope's space is one runtime container 80 km across: hashed in 4 km buckets, not 256 m ones.
            _bucketSize = ContainerRegistry.RuntimeBucketSize;
            ContainerRegistry.RuntimeBucketSize = 4096f;
        }

        private float _bucketSize;

        [TearDown]
        public void TearDown()
        {
            _persistence?.Shutdown();
            _persistence = null;
            _mesh?.Dispose();
            _mesh = null;
            _plane.Dispose();
            _store?.Dispose();
            _store = null;
            NebulaChunks.ResetForNewSession();
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.Rebuild();
            ContainerRegistry.RuntimeBucketSize = _bucketSize;
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
        }

        // ------------------------------------------------------------------------------------ the mesh

        /// <summary>Workers on the control plane, the prefabs, and the scope's own space: one root container leased to w1.</summary>
        private void StartMesh(int workers)
        {
            _mesh = new ConformanceMesh(workers, perWorkerRegistry: true);
            _mesh.Config.PersistenceRestoreGraceSeconds = 0f;
            foreach (var w in _mesh.Workers)
            {
                AttachRegistered(w.Instance, _plane);
                _plane.RegisterWorker(w.Id, w.Index, "127.0.0.1", (ushort)(7900 + w.Index));
                _plane.HeartbeatWorker(w.Id, WorkerStatus.Ready, default);
            }

            var planet = new GameObject("planet-prefab");
            planet.AddComponent<NetworkIdentity>();
            var box = planet.AddComponent<Container>();
            box.ContainerId = "planet";
            box.Size = PlanetBox;
            box.Center = Vector3.zero;
            box.OwnPhysicsFrame = true;
            box.FrameInterest = FrameInterestMode.OwnRegions;
            planet.AddComponent<NetworkTransform>();
            planet.AddComponent<PersistentEntity>();
            _planetPrefab = _mesh.RegisterPrefab(planet);

            var ship = new GameObject("ship-prefab");
            ship.AddComponent<NetworkIdentity>();
            var hull = ship.AddComponent<Container>();
            hull.ContainerId = "ship";
            hull.Size = new Vector3(10f, 6f, 20f);
            hull.Center = new Vector3(0f, 3f, 0f);
            hull.OwnPhysicsFrame = true;
            ship.AddComponent<NetworkTransform>();
            _shipPrefab = _mesh.RegisterPrefab(ship);

            var pawn = new GameObject("pawn-prefab");
            pawn.AddComponent<NetworkIdentity>();
            _pawnPrefab = _mesh.RegisterPrefab(pawn);

            var crate = new GameObject("crate-prefab");
            crate.AddComponent<NetworkIdentity>();
            crate.AddComponent<PersistentEntity>();
            _cratePrefab = _mesh.RegisterPrefab(crate);

            var space = new InstanceContainerInfo { InstanceId = ScopeKeys.Hash(Scope), ScopeKey = Scope, PartId = "space" };
            _plane.EnsureRuntimeContainer(ContainerRegistry.RuntimeContainerId(SpaceId),
                ContainerPlacement.Root(Double3.Zero, new Vector3(80000f, 80000f, 80000f)), W1.Id, space);
            Mirror();
            Assert.IsNotNull(Space, "the scope's space is registered from its row");
        }

        private static Container Space => ContainerRegistry.GetRuntime(SpaceId);

        private static void AttachRegistered(NebulaWorker worker, LocalControlPlane plane)
        {
            typeof(NebulaWorker).GetField("<ControlPlane>k__BackingField", Flags).SetValue(worker, plane);
            var registration = (WorkerRegistration)typeof(NebulaWorker).GetField("_registration", Flags).GetValue(worker);
            typeof(WorkerRegistration).GetField("<IsRegistered>k__BackingField", Flags).SetValue(registration, true);
            typeof(WorkerRegistration).GetField("_reconciledDocument", Flags).SetValue(registration, plane.DocumentId ?? "");
        }

        /// <summary>Every process's registry from the control plane's rows, as a worker's control-plane pass does it.</summary>
        private void Mirror()
        {
            // Each worker's own registry (its carried boxes and their pending leases) is mirrored on its own turn.
            foreach (var w in _mesh.Workers) w.Act(MirrorOne);
        }

        private void MirrorOne()
        {
            ContainerRegistry.SyncRuntime(_plane.Leases);
            foreach (var lease in _plane.Leases)
            {
                if (!lease.HasBounds || string.IsNullOrEmpty(lease.WorkerId)) continue;
                var owner = _mesh.Get(lease.WorkerId);
                ContainerRegistry.ApplyLease(lease.ContainerId, owner.Id, owner.Index, lease.Epoch, lease.State);
            }
            ContainerRegistry.NotifyLeasesChanged();
        }

        private void Advance(float seconds)
        {
            _seconds += seconds;
            _clock = _clock.AddSeconds(seconds);
        }

        private RuntimeGrid HostedGrid(NetworkIdentity planet)
        {
            var grid = RuntimeGrid.Hosted(Ground, Scope, GridKey, planet.Carried.ContainerId);
            NebulaChunks.Activate(grid, NebulaRoles.Worker, true, null);
            return grid;
        }

        private static RuntimeGridAllocator Allocator(ConformanceMesh.Worker worker, RuntimeGrid grid)
        {
            var definition = Ground;
            return new RuntimeGridAllocator(worker.Instance, grid)
            {
                Ring = definition.Ring,
                RetireAfterSeconds = definition.RetireSeconds,
                LeadSeconds = definition.LeadSeconds,
                Reach = definition.Reach,
            };
        }

        private NetworkIdentity SpawnIn(ConformanceMesh.Worker worker, ushort prefab, Container container, Vector3 local, ulong owner = 0)
        {
            NetworkIdentity e = null;
            worker.Act(() =>
            {
                e = NetworkPrefabs.Instantiate(prefab, local, Quaternion.identity, container.ContentRoot);
                worker.Instance.SpawnServerDriven(e, container);
            });
            e.transform.position = container.InnerSpace != null ? container.InnerSpace.Frame.LocalToSimulation(local) : container.transform.position + local;
            if (owner != 0) e.OwnerClientId = owner;
            return e;
        }

        private NetworkIdentity Authoritative(ulong netId, out ConformanceMesh.Worker owner)
        {
            foreach (var w in _mesh.Workers)
            {
                var e = w.Find(netId);
                if (e != null && e.HasAuthority) { owner = w; return e; }
            }
            owner = null;
            return null;
        }

        // ------------------------------------------------------------------------------------ the path

        private static float LegSeconds(Vector3 a, Vector3 b) => 1.5f * Vector3.Distance(a, b) / TopSpeed;

        private static float FlightSeconds()
        {
            float total = 0f;
            for (int i = 0; i + 1 < Waypoints.Length; i++) total += LegSeconds(Waypoints[i], Waypoints[i + 1]);
            return total;
        }

        private static readonly string[] Phases = { "traverse", "climb", "descent" };

        private static void PathAt(float t, out Vector3 position, out Vector3 velocity, out string phase)
        {
            for (int i = 0; i + 1 < Waypoints.Length; i++)
            {
                Vector3 a = Waypoints[i], b = Waypoints[i + 1];
                float T = LegSeconds(a, b);
                if (t >= T) { t -= T; continue; }
                float u = t / T, s = u * u * (3f - 2f * u);
                position = Vector3.Lerp(a, b, s);
                velocity = (b - a) * (6f * u * (1f - u) / T);
                phase = Phases[i];
                return;
            }
            position = Waypoints[Waypoints.Length - 1];
            velocity = Vector3.zero;
            phase = "landed";
        }

        // ------------------------------------------------------------------------------------ the lap

        private sealed class Lap
        {
            public int UnleasedTicks, ChunkSeams, Handovers, FrameCrossings, Retired, Leased, MostWanted;
            public float MinLead = float.MaxValue, MinLeadAtBoxEntry = float.MaxValue;
            public string MinLeadWhere = "", UnleasedWhere = "", MostWantedWhere = "";
            public readonly List<string> Events = new List<string>();
            public readonly HashSet<Vector3Int> LeftBehind = new HashSet<Vector3Int>();
            public int StillLeasedBehind = -1;

            public string Report()
            {
                var sb = new StringBuilder();
                sb.AppendLine($"ticks inside the box outside every chunk: {UnleasedTicks} {UnleasedWhere}");
                sb.AppendLine($"lease lead of the chunks the ship entered: min {MinLead:0.00} s ({MinLeadWhere}); at the box's top on the way down {MinLeadAtBoxEntry:0.00} s");
                sb.AppendLine($"chunks leased during the flight {Leased}, retired {Retired}; most chunks one allocator wanted at once {MostWanted} ({MostWantedWhere})");
                sb.AppendLine($"chunk seams {ChunkSeams}, authority changes {Handovers}, frame crossings {FrameCrossings}");
                sb.AppendLine($"chunks flown over on the traverse still leased after the rest, away from what stands on the ground: {StillLeasedBehind} of {LeftBehind.Count}");
                foreach (var e in Events) sb.AppendLine("  " + e);
                return sb.ToString();
            }
        }

        /// <summary>
        /// The ship never stands in the planet's box outside a leased chunk, every chunk it enters was leased well before
        /// it got there, the ground behind it retires, and the ring stays a capsule: no allocator ever wants more than a
        /// fraction of the 121 chunks a ring wide enough for the same lead would hold.
        /// </summary>
        [Test]
        public void ThePlanetsGroundIsLeasedAroundTheShipAndThePawnsOnTwoWorkers()
        {
            StartMesh(2);
            var w2 = _mesh[1];
            var planet = SpawnIn(W1, _planetPrefab, Space, PlanetAt);
            var grid = HostedGrid(planet);
            var allocators = new[] { Allocator(W1, grid), Allocator(w2, grid) };
            Assert.AreEqual(0, CountChunks(grid), "nothing is leased statically");

            var lap = new Lap();
            // When each chunk was last leased (it may retire and be leased again), and every lease made.
            var leasedAt = new Dictionary<Vector3Int, float>();
            var present = new HashSet<Vector3Int>();
            int leases = 0;
            int ticks = 0;
            uint tick = 1;

            // The far pawn's ground first, on w2; then w1's. Each worker leases around what it simulates.
            NetworkIdentity far = null, ground = null, ship = null, crew = null;
            void Step(Action script)
            {
                script?.Invoke();
                Advance(Dt);
                foreach (var w in _mesh.Workers) w.Act(() => allocators[w.Index - 1].Tick(_seconds));
                Mirror();
                foreach (var w in _mesh.Workers) w.Tick(tick);
                _mesh.Pump();
                tick++;
                ticks++;
                var now = LeasedCells(grid);
                foreach (var cell in now) if (!present.Contains(cell)) { leasedAt[cell] = _seconds; leases++; }
                present = now;
            }

            // Spawned into the space and placed by the first tick: the ground they stand on does not exist yet.
            far = SpawnIn(w2, _pawnPrefab, Space, Vector3.zero, FarClient);
            far.transform.position = planet.transform.TransformPoint(FarPawnAt);
            // Entering the planet's frame goes through its pose owner, w1 (D15), which holds the pawn for the crossing
            // and then hands it to the owner of the chunk it stands in.
            for (int i = 0; i < 45; i++) Step(null);
            Assert.That(CountChunks(grid), Is.EqualTo(9), "w2 leases a ring of one around the far pawn");
            far = Authoritative(far.NetId, out var farOwner);
            Assert.That(grid.Owns(far.Container), "the far pawn now stands in a chunk of the planet's ground");
            Assert.AreEqual(w2.Id, farOwner.Id);

            ground = SpawnIn(W1, _pawnPrefab, Space, Vector3.zero, GroundClient);
            ground.transform.position = planet.transform.TransformPoint(GroundPawnAt);
            // An allocator works four times a second: wait for its next pass, and for the pawn to step onto the ground.
            for (int i = 0; i < 45; i++) Step(null);
            ground = Authoritative(ground.NetId, out var groundOwner);
            grid.TryHostPosition(ground, out var groundAt);
            Assert.That(grid.Owns(ground.Container), $"the ground pawn stands in a chunk too (in {ground.Container?.ContainerId} at {groundAt}, {CountChunks(grid)} chunks, w1 wants {allocators[0].WantedIds.Count})");
            Assert.AreEqual(W1.Id, groundOwner.Id, "on w1, which leased it");
            var takeOff = ContainerRegistry.GetRuntime(grid.IdOf(grid.CoordOfAbsolute(Waypoints[0])));
            Assert.IsNotNull(takeOff, "the ground the ship takes off from is leased around the pawn beside it");

            ship = SpawnIn(W1, _shipPrefab, takeOff, Waypoints[0]);
            crew = SpawnIn(W1, _pawnPrefab, ship.Carried, CrewSeat, CrewClient);
            ulong shipId = ship.NetId, crewId = crew.NetId;
            for (int i = 0; i < 4; i++) Step(null);
            float flightStart = _seconds;

            // The lap.
            var frame = planet.Carried.Frame;
            int flight = Mathf.CeilToInt(FlightSeconds() / Dt);
            int rest = Mathf.CeilToInt((RetireSeconds + NebulaWorker.RuntimeTouchSeconds + 4f) / Dt);
            string lastWhere = "start", lastOwner = W1.Id;
            Vector3Int lastCell = grid.CoordOfAbsolute(Waypoints[0]);
            bool lastInBox = true;
            int crossingsBefore = W1.Instance.FrameCrossings + w2.Instance.FrameCrossings;
            int leasedBefore = leases;
            var leasedNow = new HashSet<Vector3Int>(LeasedCells(grid));
            for (int i = 0; i < flight + rest; i++)
            {
                float t = i * Dt;
                PathAt(t, out var target, out var velocity, out string phase);
                Step(() =>
                {
                    var mine = Authoritative(shipId, out _);
                    if (mine.Container != null && mine.Container.InnerSpace == planet.Carried) mine.transform.SetPositionAndRotation(frame.LocalToSimulation(target), Quaternion.identity);
                    else mine.transform.SetPositionAndRotation(planet.transform.TransformPoint(target), planet.transform.rotation);
                    mine.Motion.Velocity = velocity;
                });
                var now = Authoritative(shipId, out var owner);
                Assert.IsNotNull(now, $"t{tick}: somebody simulates the ship");
                Assert.IsNotNull(Authoritative(crewId, out _), $"t{tick}: somebody simulates the crew member");
                bool inBox = now.Container != null && now.Container.InnerSpace == planet.Carried;
                string where = grid.Owns(now.Container) && grid.TryCoordOf(now.Container.RuntimeId, out var c) ? $"chunk {c.x},{c.z}"
                    : now.Container == planet.Carried ? "planet" : now.Container == Space ? "space" : now.Container?.ContainerId ?? "none";
                if (inBox && !grid.Owns(now.Container))
                {
                    lap.UnleasedTicks++;
                    if (lap.UnleasedWhere.Length == 0) lap.UnleasedWhere = $"t{tick} {phase} at {target} in {where}";
                }
                var cellNow = grid.CoordOfAbsolute(target);
                if (inBox && (cellNow != lastCell || !lastInBox) && leasedAt.TryGetValue(cellNow, out var seenAt) && seenAt > flightStart)
                {
                    float lead = _seconds - seenAt;
                    if (lead < lap.MinLead) { lap.MinLead = lead; lap.MinLeadWhere = $"{phase}, chunk {cellNow.x},{cellNow.z} at {velocity.magnitude:0} m/s"; }
                    if (!lastInBox) lap.MinLeadAtBoxEntry = Mathf.Min(lap.MinLeadAtBoxEntry, lead);
                }
                if (where != lastWhere || owner.Id != lastOwner)
                {
                    lap.Events.Add($"t{tick} {phase}: {lastWhere}@{lastOwner} -> {where}@{owner.Id}, alt {target.y:0} m, {velocity.magnitude:0} m/s");
                    if (owner.Id != lastOwner) lap.Handovers++;
                    if (where.StartsWith("chunk") && lastWhere.StartsWith("chunk")) lap.ChunkSeams++;
                }
                for (int a = 0; a < allocators.Length; a++)
                    if (allocators[a].WantedIds.Count > lap.MostWanted) { lap.MostWanted = allocators[a].WantedIds.Count; lap.MostWantedWhere = $"w{a + 1} t{tick} {phase} {velocity.magnitude:0} m/s"; }
                if (phase == "traverse" && inBox) lap.LeftBehind.Add(cellNow);
                var leased = LeasedCells(grid);
                foreach (var cell in leasedNow) if (!leased.Contains(cell)) lap.Retired++;
                leasedNow = leased;
                lastWhere = where;
                lastOwner = owner.Id;
                lastCell = cellNow;
                lastInBox = inBox;
            }
            lap.Leased = leases - leasedBefore;
            lap.FrameCrossings = W1.Instance.FrameCrossings + w2.Instance.FrameCrossings - crossingsBefore;
            // What still stands on the ground keeps its own ring: the two pawns and the ship that landed.
            var kept = new HashSet<Vector3Int>();
            foreach (var standing in new[] { Authoritative(far.NetId, out _), Authoritative(ground.NetId, out _), Authoritative(shipId, out _) })
            {
                var around = new List<Vector3Int>();
                grid.Neighborhood(grid.CoordOf(standing), Ground.Ring, around);
                kept.UnionWith(around);
            }
            lap.StillLeasedBehind = 0;
            foreach (var cell in lap.LeftBehind) if (leasedNow.Contains(cell) && !kept.Contains(cell)) lap.StillLeasedBehind++;
            TestContext.WriteLine(lap.Report());

            Assert.AreEqual(0, lap.UnleasedTicks, "the ship never stands in the planet's box outside a leased chunk: " + lap.UnleasedWhere);
            Assert.That(lap.MinLead, Is.GreaterThanOrEqualTo(1.5f), "every chunk the ship entered was leased at least 1.5 s before: " + lap.MinLeadWhere);
            Assert.That(lap.MinLeadAtBoxEntry, Is.GreaterThanOrEqualTo(1.5f).And.LessThan(float.MaxValue), "including the ground it came down to");
            Assert.That(lap.ChunkSeams, Is.GreaterThanOrEqualTo(4), "chunk seams crossed at speed");
            Assert.That(lap.Handovers, Is.GreaterThanOrEqualTo(2), "into w2's ground and back to w1");
            Assert.AreEqual(2, lap.FrameCrossings, "out of the planet's frame once and back in once");
            Assert.That(lap.Retired, Is.GreaterThan(0), "the ground behind the ship retires");
            Assert.That(lap.LeftBehind.Count, Is.GreaterThan(0));
            Assert.AreEqual(0, lap.StillLeasedBehind, "every chunk the ship flew over away from the pawns is gone after the rest");
            Assert.That(lap.MostWanted, Is.LessThanOrEqualTo(40), "a capsule along the velocity, not a ring wide enough for the lead all round");

            far = Authoritative(far.NetId, out _);
            ground = Authoritative(ground.NetId, out _);
            Assert.That(grid.Owns(far.Container) && grid.Owns(ground.Container), "both pawns still stand on leased ground");
        }

        /// <summary>
        /// A hosted grid (D22) under a turning host (D20, NEB-390): the planet turns about its axis, at half a degree a
        /// second and at a real planet's rate (1e-4 rad/s, once in about 17 hours). A pawn standing in a hosted chunk keeps
        /// its chunk and its place in the planet's frame to the millimetre while its position in the scope turns with
        /// the planet; and a ship climbing straight up out of the planet's box leaves it moving at v + ω × r in the
        /// scope's space, as the planet's turning ground carried it.
        /// </summary>
        [TestCase(0.5f, TestName = "AHostedGridTurnsWithItsHost(half a degree a second)")]
        [TestCase(0.0057295779f, TestName = "AHostedGridTurnsWithItsHost(1e-4 rad per second)")]
        public void AHostedGridTurnsWithItsHost(float degreesPerSecond)
        {
            StartMesh(1);
            var planet = SpawnIn(W1, _planetPrefab, Space, PlanetAt);
            var grid = HostedGrid(planet);
            var allocator = Allocator(W1, grid);
            var frame = planet.Carried.Frame;
            var omega = new Vector3(0f, degreesPerSecond * Mathf.Deg2Rad, 0f);
            uint tick = 1;
            void Step(Action script)
            {
                planet.transform.rotation = Quaternion.Euler(0f, 30f + degreesPerSecond * tick * Dt, 0f);
                script?.Invoke();
                Advance(Dt);
                W1.Act(() => allocator.Tick(_seconds));
                Mirror();
                W1.Tick(tick);
                _mesh.Pump();
                tick++;
            }

            var pawn = SpawnIn(W1, _pawnPrefab, Space, Vector3.zero, GroundClient);
            pawn.transform.position = Quaternion.Euler(0f, 30f, 0f) * GroundPawnAt + PlanetAt;
            for (int i = 0; i < 45; i++) Step(null);
            pawn = Authoritative(pawn.NetId, out _);
            Assert.That(grid.Owns(pawn.Container), "the pawn stands in a chunk of the planet's ground");
            var chunk = pawn.Container;
            var local = frame.SimulationToLocal(pawn.transform.position);

            // Five seconds standing still on the turning ground.
            float drift = 0f, scopeError = 0f;
            var startScope = PhysicsFrames.ToScope(pawn.transform.position, pawn.Space);
            var scope = startScope;
            for (int i = 0; i < 300; i++)
            {
                Step(null);
                pawn = Authoritative(pawn.NetId, out _);
                Assert.AreSame(chunk, pawn.Container, $"t{tick}: the pawn stays in its chunk");
                drift = Mathf.Max(drift, Vector3.Distance(local, frame.SimulationToLocal(pawn.transform.position)));
                scope = PhysicsFrames.ToScope(pawn.transform.position, pawn.Space);
                scopeError = Mathf.Max(scopeError, Vector3.Distance(scope, PlanetAt + planet.transform.rotation * local));
            }
            float turned = Vector3.Distance(startScope, scope);
            float arc = omega.y * 300 * Dt * new Vector2(local.x, local.z).magnitude;
            TestContext.WriteLine($"{degreesPerSecond} deg/s: pawn drift in the planet's frame {drift * 1000f:0.000} mm; moved {turned:0.000} m in the scope (an arc of {arc:0.000} m), {scopeError * 1000f:0.0} mm from where the planet's turn puts it");
            Assert.That(drift, Is.LessThan(0.001f), "the pawn keeps its place in the planet's frame");
            Assert.That(scopeError, Is.LessThan(0.01f), "and turns with the planet in the scope");
            Assert.That(turned, Is.EqualTo(arc).Within(Mathf.Max(0.01f, arc * 0.01f)), "by the arc the planet turned through");

            // A ship takes off beside the pawn and climbs straight up out of the box at 100 m/s.
            var takeOff = new Vector3(local.x + 40f, 0f, local.z);
            var ship = SpawnIn(W1, _shipPrefab, chunk, takeOff);
            ulong shipId = ship.NetId;
            var climb = new Vector3(0f, 100f, 0f);
            float t0 = _seconds;
            Vector3 lastTarget = takeOff;
            bool left = false;
            float velocityError = -1f, spin = 0f;
            string where = "";
            for (int i = 0; i < 20 * 60 && !left; i++)
            {
                var target = takeOff + climb * (_seconds - t0);
                lastTarget = target;
                Step(() =>
                {
                    var mine = Authoritative(shipId, out _);
                    if (mine.Container != null && mine.Container.InnerSpace == planet.Carried)
                    {
                        mine.transform.SetPositionAndRotation(frame.LocalToSimulation(target), Quaternion.identity);
                        mine.Motion.Velocity = climb;
                    }
                });
                var now = Authoritative(shipId, out _);
                if (now.Container != Space) continue;
                left = true;
                var r = planet.transform.rotation * planet.transform.InverseTransformPoint(now.transform.position);
                var expected = planet.transform.rotation * climb + Vector3.Cross(omega, r);
                spin = Vector3.Cross(omega, r).magnitude;
                velocityError = Vector3.Distance(expected, now.Motion.Velocity);
                where = $"t{tick} at {planet.transform.InverseTransformPoint(now.transform.position)} planet-local: expected {expected}, got {now.Motion.Velocity}; frame read ω {frame.State.AngularVelocity.y:0.0000000} rad/s of {omega.y:0.0000000}";
            }
            TestContext.WriteLine($"ship leaving the box: ω × r {spin:0.0000} m/s, velocity error {velocityError * 1000f:0.0} mm/s ({where})");
            Assert.IsTrue(left, "the ship left the planet's box into the scope's space (last target " + lastTarget + ")");
            Assert.That(velocityError, Is.LessThan(0.02f), "and left it moving at v + ω × r: " + where);
        }

        private static int CountChunks(RuntimeGrid grid)
        {
            int n = 0;
            foreach (var c in ContainerRegistry.Runtime) if (c != null && grid.Owns(c)) n++;
            return n;
        }

        private static HashSet<Vector3Int> LeasedCells(RuntimeGrid grid)
        {
            var cells = new HashSet<Vector3Int>();
            foreach (var c in ContainerRegistry.Runtime)
                if (c != null && grid.Owns(c) && grid.TryCoordOf(c.RuntimeId, out var cell)) cells.Add(cell);
            return cells;
        }

        // ------------------------------------------------------------------------------------ the restart

        private void AttachPersistence(LocalPersistenceStore store)
        {
            // The constructor dates the leases it finds on the default clock, so the test's clock runs on from it.
            _persistence = new NebulaPersistence(W1.Instance, _mesh.Config, store);
            float start = Time.unscaledTime - _seconds;
            _persistence.Now = () => start + _seconds;
            typeof(NebulaWorker).GetProperty(nameof(NebulaWorker.Persistence)).SetValue(W1.Instance, _persistence);
        }

        /// <summary>
        /// A crate standing in a hosted chunk is saved under the chunk's container id and its hosted part id. After a
        /// restart that keeps the control plane and the store, the planet comes back first, from its record in the scope's
        /// space, under a new container id; the grid is hosted again by that id; the chunk row still naming the old
        /// container is replaced; and the crate comes back in the chunk, under the planet, where it stood.
        /// </summary>
        [Test]
        public void ARestartRestoresAnEntityStandingInAHostedChunk()
        {
            _store = new LocalPersistenceStore(); // memory backend
            _store.Connect();
            StartMesh(1);
            AttachPersistence(_store);
            var planet = SpawnIn(W1, _planetPrefab, Space, PlanetAt);
            _persistence.SaveNow(planet);
            string planetKey = planet.Persistent.Key;
            string oldHost = planet.Carried.ContainerId;
            var grid = HostedGrid(planet);
            var allocator = Allocator(W1, grid);
            var coord = grid.CoordOfAbsolute(CrateAt);
            Container chunk = null;
            W1.Act(() => allocator.EnsureContainer(coord, c => chunk = c));
            Mirror();
            Assert.IsNotNull(chunk, "the chunk is leased on demand");
            Assert.AreSame(planet.Carried, chunk.Parent, "as a child of the planet's container");
            Assert.AreEqual(ChunkKeys.ContainerId(GridKey, coord), chunk.ContainerId, "named by the grid's key and the coordinate");
            Assert.AreEqual(Scope, chunk.ScopeKey, "in the planet's scope");
            var crate = SpawnIn(W1, _cratePrefab, chunk, CrateAt);
            _persistence.SaveNow(crate);
            string crateKey = crate.Persistent.Key;
            PersistedEntityRecord saved = null;
            _store.Load(crateKey, r => saved = r);
            _store.Tick();
            Assert.IsNotNull(saved);
            Assert.AreEqual(chunk.ContainerId, saved.ContainerId);
            Assert.AreEqual(ChunkKeys.HostedPartId(GridKey, coord), saved.PartId, "the record names the grid and the chunk");
            Assert.That(ChunkKeys.TryHostedCoordOf(saved, out var savedGrid, out var savedCoord), Is.True);
            Assert.AreEqual(GridKey, savedGrid);
            Assert.AreEqual(coord, savedCoord);
            Assert.That(ChunkKeys.TryCoordOf(saved, out _), Is.False, "and is not mistaken for a root chunk of the scope");

            // The restart: every process goes; the control plane and the store stay.
            _persistence.Shutdown();
            _persistence = null;
            _mesh.Dispose();
            NebulaChunks.ResetForNewSession();
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.Rebuild();
            StartMesh(1);
            AttachPersistence(_store);
            Assert.IsNull(ContainerRegistry.GetRuntime(chunk.RuntimeId), "the chunk's row waits for a parent that is not here");
            // Something spawns before the planet comes back, so it comes back under another net id and container id.
            SpawnIn(W1, _pawnPrefab, Space, new Vector3(100f, 0f, 0f));

            NetworkIdentity restoredPlanet = null;
            for (int i = 0; i < 10 && restoredPlanet == null; i++)
            {
                Advance(Dt);
                _persistence.Update();
                _store.Tick();
                _persistence.Update();
                restoredPlanet = _persistence.Find(planetKey);
            }
            int inSpace = -1;
            _store.CountRecords(Scope, Space.ContainerId, n => inSpace = n);
            _store.Tick();
            Assert.IsNotNull(restoredPlanet, $"the planet comes back from its record in the space (fenced {W1.Instance.IsFenced}, loads {_persistence.RestoreLoadsInFlight}, "
                + $"space restored {_persistence.IsContainerRestored(Space.ContainerId)}, space owned {Space.IsOwnedBy(W1.Id)}, records in the space {inSpace}, restored {_persistence.RestoredCount})");
            Assert.AreNotEqual(oldHost, restoredPlanet.Carried.ContainerId, "under a new container id");

            grid = HostedGrid(restoredPlanet);
            allocator = Allocator(W1, grid);
            Container again = null;
            for (int i = 0; i < 4 && again == null; i++)
            {
                W1.Act(() => allocator.EnsureContainer(coord, c => again = c));
                Mirror();
            }
            Assert.IsNotNull(again, "the chunk is leased again under the planet that came back");
            Assert.AreSame(restoredPlanet.Carried, again.Parent);
            Assert.AreEqual(restoredPlanet.Carried.ContainerId, _plane.FindLease(again.ContainerId).ParentId, "its row was replaced");

            NetworkIdentity restoredCrate = null;
            for (int i = 0; i < 10 && restoredCrate == null; i++)
            {
                Advance(Dt);
                _persistence.Update();
                _store.Tick();
                _persistence.Update();
                restoredCrate = _persistence.Find(crateKey);
            }
            Assert.IsNotNull(restoredCrate, "the crate comes back with its chunk");
            Assert.AreSame(again, restoredCrate.Container, "in the chunk");
            Assert.That(Vector3.Distance(CrateAt, restoredPlanet.Carried.Frame.SimulationToLocal(restoredCrate.transform.position)), Is.LessThan(0.001f),
                "where it stood on the planet");
        }
    }
}
