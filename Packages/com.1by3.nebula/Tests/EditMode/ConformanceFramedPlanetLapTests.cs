using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// The planet → space → planet lap (<c>docs/container-tree.md</c> §8), re-run on the current implementation as one
    /// scope. A flat planet is a carrier with a physics frame and regions of its own (<see cref="FrameInterestMode.OwnRegions"/>);
    /// its ground is four leased runtime chunks under it, dealt alternately to two workers. A ship with its own frame and
    /// a crew member aboard takes off beside a second pawn standing on the ground, climbs out of the planet's box to
    /// 15 km at up to 300 m/s, comes back down and lands four kilometres away, crossing every chunk seam on the way. The
    /// lap is flown with the planet still, then turning at half a degree a second.
    /// <para>
    /// Measured each tick on the workers: the ship's position and velocity against the scripted path (planet-local),
    /// who simulates it, which container it is in, the crew member's pose in the ship's frame and how long each
    /// worker's tick took. Measured four times a second on a real <see cref="NebulaGateway"/> in the same process, fed
    /// each entity's spawn as its authoritative worker writes it: what the ground pawn's client and the crew member's
    /// client have in their interest sets.
    /// </para>
    /// <para>
    /// Tier B (<see cref="ConformanceMesh"/>): the container registry and the frames are process-wide, the gateway's
    /// lease rows are written in directly (as <c>OnControlPlaneChanged</c> writes them) and its entity records come from
    /// re-sent spawns rather than state entries. There is no client process: a client's view is its interest set.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceFramedPlanetLapTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type GatewayType = typeof(NebulaGateway);
        private const float Dt = NetworkTime.TickInterval;

        /// <summary>The planet's box: 8 km square, from 400 m below the ground to 600 m above it.</summary>
        private static readonly Vector3 PlanetBox = new Vector3(8000f, 1000f, 8000f);
        private static readonly Vector3 PlanetBoxCenter = new Vector3(0f, 100f, 0f);
        private static readonly Vector3 PlanetAt = new Vector3(30000f, 0f, 0f);
        private const float ChunkWidth = 2000f;
        private const float TopSpeed = 300f;
        private const float Ceiling = 15000f;
        private const float TurnDegreesPerSecond = 0.5f;
        private const ulong GroundClient = 41, CrewClient = 42;
        private const int GatewayEvery = 15; // ticks: 4 Hz, the gateway's interest rate

        /// <summary>
        /// The lap, planet-local: take off in chunk 1 (w2), cross the seams at x = 0 (w2 | w1) and x = 2000 (w1 | w2)
        /// 300 m up, climb out of the box over chunk 3 (w2, not the planet's pose owner) to 15 km, and come back down
        /// into chunk 1 (through w1, the pose owner) to land 60 m from where it took off.
        /// </summary>
        private static readonly Vector3[] Waypoints =
        {
            new Vector3(-1000f, 0f, 0f),
            new Vector3(3000f, 300f, 0f),
            new Vector3(0f, Ceiling, 0f),
            new Vector3(-970f, 0f, 0f),
        };
        private static Vector3 TakeOff => Waypoints[0];
        private static readonly Vector3 GroundPawnAt = new Vector3(-1030f, 1f, 0f);
        private static readonly Vector3 CrewSeat = new Vector3(0f, 1f, 6f);

        private ConformanceMesh _mesh;
        private Container _space;
        private ushort _planetPrefab, _shipPrefab, _pawnPrefab;
        private NetworkIdentity _planet;
        private readonly List<Container> _chunks = new List<Container>();
        private GameObject _gatewayHost;
        private readonly List<GameObject> _clientObjects = new List<GameObject>();
        private NebulaGateway _gateway;

        private ConformanceMesh.Worker W1 => _mesh[0];
        private ConformanceMesh.Worker W2 => _mesh[1];

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
            _mesh = new ConformanceMesh(2);
            // No ghost band. The container registry is process-wide here, so a ghost of the ship on the other worker
            // re-registers the ship's box and evacuates the crew from the authoritative copy's box into the ground
            // (ContainerRegistry.UnregisterDynamic): a harness artifact, not what two processes do. Handovers still
            // run at every seam; holding the neighbour warm before the crossing is what this leaves out.
            _mesh.Config.GhostBandMargin = -1f;
            _space = _mesh.AddStaticContainer("system", Vector3.zero, new Vector3(200000f, 200000f, 200000f));
            _space.Center = Vector3.zero;
            ContainerRegistry.Rebuild();
            _mesh.SetOwner(_space, W1);

            var planet = new GameObject("planet-prefab");
            planet.AddComponent<NetworkIdentity>();
            var box = planet.AddComponent<Container>();
            box.ContainerId = "planet";
            box.Size = PlanetBox;
            box.Center = PlanetBoxCenter;
            box.OwnPhysicsFrame = true;
            box.FrameInterest = FrameInterestMode.OwnRegions;
            planet.AddComponent<NetworkTransform>();
            _planetPrefab = _mesh.RegisterPrefab(planet);

            var ship = new GameObject("ship-prefab");
            var shipId = ship.AddComponent<NetworkIdentity>();
            shipId.RelevanceRadius = 4096f; // asks for more than InterestMaxRadius: the gateway clamps it
            var hull = ship.AddComponent<Container>();
            hull.ContainerId = "ship";
            hull.Size = new Vector3(10f, 6f, 20f);
            hull.Center = new Vector3(0f, 3f, 0f);
            hull.OwnPhysicsFrame = true;
            ship.AddComponent<NetworkTransform>();
            _shipPrefab = _mesh.RegisterPrefab(ship);

            var pawn = new GameObject("pawn-prefab");
            var pawnId = pawn.AddComponent<NetworkIdentity>();
            pawnId.RelevanceRadius = 4096f;
            _pawnPrefab = _mesh.RegisterPrefab(pawn);

            _planet = W1.SpawnServerDriven(_planetPrefab, _space, PlanetAt, Quaternion.identity);
            // Four chunks of ground in a row along x, each 2 km wide and as tall as the planet's box, alternating
            // between the workers: seams at x = -2000 (w1 | w2), 0 (w2 | w1) and 2000 (w1 | w2).
            for (int i = 0; i < 4; i++)
            {
                var center = new Vector3(-3000f + ChunkWidth * i, PlanetBoxCenter.y, 0f);
                var chunk = ContainerRegistry.RegisterRuntime(1000UL + (ulong)i,
                    ContainerPlacement.Child(_planet.Carried.ContainerId, center, new Vector3(ChunkWidth, PlanetBox.y, PlanetBox.z), ContainerAuthority.Leased));
                Assert.IsNotNull(chunk);
                var owner = (i & 1) == 0 ? W1 : W2;
                ContainerRegistry.ApplyLease(chunk.ContainerId, owner.Id, owner.Index, 1);
                _chunks.Add(chunk);
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (_gatewayHost != null) Object.DestroyImmediate(_gatewayHost);
            foreach (var go in _clientObjects) if (go != null) Object.DestroyImmediate(go);
            _clientObjects.Clear();
            _chunks.Clear();
            _mesh.Dispose();
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
        }

        // ------------------------------------------------------------------------------------ the path

        /// <summary>One leg: from a to b with a smoothstep in time, so it starts and ends at rest and peaks at <see cref="TopSpeed"/>.</summary>
        private static float LegSeconds(Vector3 a, Vector3 b) => 1.5f * Vector3.Distance(a, b) / TopSpeed;

        private static readonly string[] Phases = { "traverse", "climb", "descent" };

        private static float FlightSeconds()
        {
            float total = 0f;
            for (int i = 0; i + 1 < Waypoints.Length; i++) total += LegSeconds(Waypoints[i], Waypoints[i + 1]);
            return total;
        }

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

        // ------------------------------------------------------------------------------------ the record

        private sealed class Event
        {
            public uint Tick;
            public string What;
            public override string ToString() => $"t{Tick} ({Tick * Dt:0.0} s): {What}";
        }

        /// <summary>A client's view of one entity: when it gained and lost it, and whether the gateway subscribed its region.</summary>
        private sealed class View
        {
            public readonly List<string> Lines = new List<string>();
            public bool Held, Subscribed;
            /// <summary>Distances at the first loss and first regain of the interest set, and of the region subscription.</summary>
            public float LostAt = -1f, RegainedAt = -1f, UnsubscribedAt = -1f, ResubscribedAt = -1f;
            public string LostWhere = "", RegainedWhere = "";

            public void Observe(uint tick, string phase, bool held, bool subscribed, float distance, string where, string what)
            {
                if (held != Held)
                {
                    Lines.Add($"t{tick} {phase}: {(held ? "gained" : "lost")} {what} at {distance:0.0} m ({where})");
                    if (!held && LostAt < 0) { LostAt = distance; LostWhere = where; }
                    if (held && LostAt >= 0 && RegainedAt < 0) { RegainedAt = distance; RegainedWhere = where; }
                    Held = held;
                }
                if (subscribed != Subscribed)
                {
                    Lines.Add($"t{tick} {phase}: region of {what} {(subscribed ? "subscribed" : "unsubscribed")} at {distance:0.0} m ({where})");
                    if (!subscribed && UnsubscribedAt < 0) UnsubscribedAt = distance;
                    if (subscribed && UnsubscribedAt >= 0 && ResubscribedAt < 0) ResubscribedAt = distance;
                    Subscribed = subscribed;
                }
            }
        }

        private struct Sample
        {
            public uint Tick;
            public ushort Worker;
            public EntityStateEntry Ship;
            public Vector3 PlanetPosition;
            public Quaternion PlanetRotation;
            public Vector3 Truth;
        }

        /// <summary>The ship's state as its worker streams it this tick, through the wire codec.</summary>
        private static EntityStateEntry Sent(NetworkIdentity e)
        {
            var entry = EntityStateEntry.Snapshot(e);
            entry.Fields |= TransformFields.Location | TransformFields.Reliable;
            var w = new NetworkWriter();
            entry.Write(w);
            return EntityStateEntry.Read(new NetworkReader(w.ToSegment()));
        }

        private sealed class Lap
        {
            public readonly List<Event> Events = new List<Event>();
            public float MaxPositionError, MaxVelocityError, MaxCrewError;
            public uint MaxPositionErrorTick, MaxVelocityErrorTick;
            public string MaxPositionErrorWhere = "", MaxVelocityErrorWhere = "";
            public readonly List<string> SeamErrors = new List<string>();
            public int FrameCrossings, CrossingHandoffs, Handovers, ChunkSeams, CrewOffShipTicks;
            public double MedianTickMs, MaxTickMs;
            public uint MaxTickMsTick;
            public bool PlanetGhostedOnW2;
            /// <summary>The planet frame's angular velocity (y) as each worker's own sample left it, against the truth.</summary>
            public float OmegaTruth, OmegaAfterW1Min = float.MaxValue, OmegaAfterW1Max = float.MinValue, OmegaAfterW2Max = float.MinValue;
            public readonly List<string> Table = new List<string>();
            /// <summary>What a client is sent of the ship each tick, by whom, and where the planet and the ship really were.</summary>
            public readonly List<Sample> Stream = new List<Sample>();
            public readonly List<uint> CrossingTicks = new List<uint>();
            public readonly View GroundSeesShip = new View(), CrewSeesGround = new View();
            public float ShipAltitudeLeavingBox = -1f, ShipAltitudeEnteringBox = -1f;
            public int CrewKnownChunksInSpace = -1, CrewKnownChunksInBox = -1;

            public void Add(uint tick, string what) => Events.Add(new Event { Tick = tick, What = what });

            public string Report()
            {
                var sb = new StringBuilder();
                sb.AppendLine($"max position error {MaxPositionError * 1000f:0.000} mm at t{MaxPositionErrorTick} ({MaxPositionErrorWhere})");
                sb.AppendLine($"max velocity error {MaxVelocityError:0.0000} m/s at t{MaxVelocityErrorTick} ({MaxVelocityErrorWhere})");
                sb.AppendLine($"max crew drift in the ship's frame {MaxCrewError * 1000f:0.000} mm; ticks the crew member was not in the ship's box: {CrewOffShipTicks}");
                sb.AppendLine($"frame crossings {FrameCrossings}, crossing hand-offs {CrossingHandoffs}, chunk changes {ChunkSeams}, authority changes {Handovers}; planet ghosted on w2: {PlanetGhostedOnW2}");
                sb.AppendLine($"planet frame omega.y: truth {OmegaTruth:0.000000} rad/s; after w1's sample {OmegaAfterW1Min:0.000000}..{OmegaAfterW1Max:0.000000}; after w2's sample (same tick, same pose) max {OmegaAfterW2Max:0.000000}");
                sb.AppendLine($"worker tick: median {MedianTickMs:0.000} ms, max {MaxTickMs:0.000} ms at t{MaxTickMsTick}");
                sb.AppendLine($"box: left at {ShipAltitudeLeavingBox:0.00} m, entered at {ShipAltitudeEnteringBox:0.00} m");
                sb.AppendLine($"ground client / ship: lost at {GroundSeesShip.LostAt:0.0} m ({GroundSeesShip.LostWhere}), regained at {GroundSeesShip.RegainedAt:0.0} m ({GroundSeesShip.RegainedWhere}); region unsubscribed at {GroundSeesShip.UnsubscribedAt:0.0} m, resubscribed at {GroundSeesShip.ResubscribedAt:0.0} m");
                sb.AppendLine($"crew client / ground pawn: lost at {CrewSeesGround.LostAt:0.0} m ({CrewSeesGround.LostWhere}), regained at {CrewSeesGround.RegainedAt:0.0} m ({CrewSeesGround.RegainedWhere}); region unsubscribed at {CrewSeesGround.UnsubscribedAt:0.0} m, resubscribed at {CrewSeesGround.ResubscribedAt:0.0} m");
                sb.AppendLine($"crew client: chunk rows known on the ground {CrewKnownChunksInBox}, just after leaving the box {CrewKnownChunksInSpace}");
                sb.AppendLine("seams (every change of container or worker):");
                foreach (var l in SeamErrors) sb.AppendLine("  " + l);
                sb.AppendLine("events:");
                foreach (var e in Events) sb.AppendLine("  " + e);
                sb.AppendLine("ground client:");
                foreach (var l in GroundSeesShip.Lines) sb.AppendLine("  " + l);
                sb.AppendLine("crew client:");
                foreach (var l in CrewSeesGround.Lines) sb.AppendLine("  " + l);
                sb.AppendLine("samples:");
                foreach (var l in Table) sb.AppendLine("  " + l);
                return sb.ToString();
            }
        }

        // ------------------------------------------------------------------------------------ the run

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

        private string Where(NetworkIdentity e)
        {
            var c = e.Container;
            if (c == null) return "none";
            if (c == _space) return "space";
            int i = _chunks.IndexOf(c);
            if (i >= 0) return $"chunk{i}";
            if (c == _planet.Carried) return "planet";
            return c.ContainerId;
        }

        private bool InPlanetFrame(NetworkIdentity e) => e.Container != null && e.Container.InnerSpace == _planet.Carried;

        /// <summary>The ship's position in the planet's own coordinates, wherever it is.</summary>
        private Vector3 PlanetLocal(NetworkIdentity e) => InPlanetFrame(e)
            ? _planet.Carried.Frame.SimulationToLocal(e.transform.position)
            : _planet.transform.InverseTransformPoint(e.transform.position);

        /// <summary>A planet-local velocity at a planet-local point, in the space the entity is in.</summary>
        private Vector3 VelocityInSpaceOf(NetworkIdentity e, Vector3 local, Vector3 localVelocity, Vector3 omega)
        {
            if (InPlanetFrame(e)) return localVelocity;
            var r = _planet.transform.rotation * local;
            return _planet.transform.rotation * localVelocity + Vector3.Cross(omega, r);
        }

        private NetworkIdentity SpawnIn(ConformanceMesh.Worker worker, ushort prefab, Container container, Vector3 local)
        {
            NetworkIdentity e = null;
            worker.Act(() =>
            {
                e = NetworkPrefabs.Instantiate(prefab, local, Quaternion.identity, container.ContentRoot);
                worker.Instance.SpawnServerDriven(e, container);
            });
            e.transform.position = container.InnerSpace.Frame.LocalToSimulation(local);
            return e;
        }

        private Lap Fly(bool turning, bool withGateway)
        {
            var lap = new Lap();
            var frame = _planet.Carried.Frame;
            Assert.IsNotNull(frame, "the planet has a physics frame");

            // The ship, landed in chunk 1 (w2), with a crew member aboard; a second pawn on the ground 30 m away.
            var start = _chunks[1];
            var ship = SpawnIn(W2, _shipPrefab, start, TakeOff);
            var ground = SpawnIn(W2, _pawnPrefab, start, GroundPawnAt);
            Assert.IsNotNull(ship.Carried.Frame, "the ship has a physics frame");
            var crew = SpawnIn(W2, _pawnPrefab, ship.Carried, CrewSeat);
            ulong shipId = ship.NetId, crewId = crew.NetId, groundId = ground.NetId;

            object groundClient = null, crewClient = null;
            if (withGateway)
            {
                StartGateway();
                groundClient = Client(GroundClient, 7);
                crewClient = Client(CrewClient, 8);
            }

            int settle = 30, rest = 90;
            int flight = Mathf.CeilToInt(FlightSeconds() / Dt);
            var omega = turning ? new Vector3(0f, TurnDegreesPerSecond * Mathf.Deg2Rad, 0f) : Vector3.zero;
            lap.OmegaTruth = omega.y;
            var tickMs = new List<double>();
            string lastWhere = Where(ship), lastOwner = W2.Id;
            bool lastInBox = true;
            string lastCrewWhere = Where(crew) + "@" + W2.Id;
            lap.Add(0, $"spawned: ship {Where(ship)}, ground pawn {Where(ground)}, crew {lastCrewWhere}");
            int crossingsBefore = W1.Instance.FrameCrossings + W2.Instance.FrameCrossings;
            int handoffsBefore = W1.Instance.CrossingHandoffs + W2.Instance.CrossingHandoffs;
            uint tick = 1;
            for (int i = -settle; i < flight + rest; i++, tick++)
            {
                float t = Mathf.Max(0, i) * Dt;
                PathAt(t, out var target, out var targetVelocity, out string phase);

                if (turning) _planet.transform.rotation = Quaternion.Euler(0f, TurnDegreesPerSecond * tick * Dt, 0f);
                var mine = Authoritative(shipId, out var owner);
                Assert.IsNotNull(mine, $"t{tick}: somebody simulates the ship");
                // The script: the ship follows the path in the planet's coordinates, in whichever space it is in.
                if (InPlanetFrame(mine)) mine.transform.SetPositionAndRotation(frame.LocalToSimulation(target), Quaternion.identity);
                else mine.transform.SetPositionAndRotation(_planet.transform.TransformPoint(target), _planet.transform.rotation);
                mine.Motion.Velocity = VelocityInSpaceOf(mine, target, targetVelocity, omega);

                var sw = Stopwatch.StartNew();
                W1.Tick(tick);
                float omegaW1 = frame.State.HasRates ? frame.State.AngularVelocity.y : float.NaN;
                W2.Tick(tick);
                float omegaW2 = frame.State.HasRates ? frame.State.AngularVelocity.y : float.NaN;
                _mesh.Pump();
                double ms = sw.Elapsed.TotalMilliseconds;
                tickMs.Add(ms);
                if (ms > lap.MaxTickMs) { lap.MaxTickMs = ms; lap.MaxTickMsTick = tick; }
                if (i > 0 && !float.IsNaN(omegaW1))
                {
                    lap.OmegaAfterW1Min = Mathf.Min(lap.OmegaAfterW1Min, omegaW1);
                    lap.OmegaAfterW1Max = Mathf.Max(lap.OmegaAfterW1Max, omegaW1);
                    lap.OmegaAfterW2Max = Mathf.Max(lap.OmegaAfterW2Max, omegaW2);
                }

                mine = Authoritative(shipId, out owner);
                Assert.IsNotNull(mine, $"t{tick}: somebody simulates the ship after the tick");
                if (W2.Find(_planet.NetId) != null) lap.PlanetGhostedOnW2 = true;
                string where = Where(mine);
                var actual = PlanetLocal(mine);
                float error = Vector3.Distance(target, actual);
                var expectedVelocity = VelocityInSpaceOf(mine, target, targetVelocity, omega);
                float velocityError = Vector3.Distance(expectedVelocity, mine.Motion.Velocity);
                bool crossed = where != lastWhere || owner.Id != lastOwner;
                if (error > lap.MaxPositionError) { lap.MaxPositionError = error; lap.MaxPositionErrorTick = tick; lap.MaxPositionErrorWhere = $"{phase} {lastWhere}->{where} {owner.Id}"; }
                if (velocityError > lap.MaxVelocityError) { lap.MaxVelocityError = velocityError; lap.MaxVelocityErrorTick = tick; lap.MaxVelocityErrorWhere = $"{phase} {lastWhere}->{where} {owner.Id} expected {expectedVelocity} got {mine.Motion.Velocity}"; }
                if (crossed)
                {
                    lap.SeamErrors.Add($"t{tick} {phase}: {lastWhere}@{lastOwner} -> {where}@{owner.Id} at planet-local ({actual.x:0.00}, {actual.y:0.00}, {actual.z:0.00}), speed {targetVelocity.magnitude:0.0} m/s, position error {error * 1000f:0.000} mm, velocity error {velocityError:0.0000} m/s");
                    if (owner.Id != lastOwner) lap.Handovers++;
                    if (where != lastWhere && where.StartsWith("chunk") && lastWhere.StartsWith("chunk")) lap.ChunkSeams++;
                }
                bool inBox = InPlanetFrame(mine);
                if (lastInBox && !inBox) lap.ShipAltitudeLeavingBox = actual.y;
                if (!lastInBox && inBox) lap.ShipAltitudeEnteringBox = actual.y;
                lastInBox = inBox;

                // The crew member stays in its seat in the ship's frame, whoever simulates either.
                var crewMine = Authoritative(crewId, out var crewOwner);
                Assert.IsNotNull(crewMine, $"t{tick}: somebody simulates the crew member");
                string crewWhere = Where(crewMine) + "@" + crewOwner.Id;
                if (crewWhere != lastCrewWhere)
                {
                    var crewLocal = crewMine.Container != null && crewMine.Container.InnerSpace == _planet.Carried ? PlanetLocal(crewMine) : crewMine.transform.position;
                    lap.Add(tick, $"crew {lastCrewWhere} -> {crewWhere} (ship {where}@{owner.Id}), crew at {crewLocal}");
                    lastCrewWhere = crewWhere;
                }
                if (crewMine.Container == null || !crewMine.Container.IsDynamic || crewMine.Container.CarrierNetId != shipId) lap.CrewOffShipTicks++;
                if (crewMine.Container != null && crewMine.Container.Frame != null)
                {
                    float crewError = Vector3.Distance(CrewSeat, crewMine.Container.Frame.SimulationToLocal(crewMine.transform.position));
                    lap.MaxCrewError = Mathf.Max(lap.MaxCrewError, crewError);
                }
                if (where != lastWhere && (where == "space" || lastWhere == "space")) lap.CrossingTicks.Add(tick);
                ushort streamer = owner.Index;
                var streamed = mine;
                owner.Act(() => lap.Stream.Add(new Sample
                {
                    Tick = tick, Worker = streamer, Ship = Sent(streamed), PlanetPosition = _planet.transform.position,
                    PlanetRotation = _planet.transform.rotation, Truth = _planet.transform.TransformPoint(actual),
                }));
                if (crossed || tick % 600 == 0) lap.Table.Add($"t{tick} {phase} {owner.Id} {where} target {target} error {error * 1000f:0.000} mm velocity error {velocityError:0.0000}");
                lastWhere = where;
                lastOwner = owner.Id;

                if (withGateway && tick % GatewayEvery == 0)
                {
                    var groundMine = Authoritative(groundId, out var groundOwner);
                    Announce(W1, _planet);
                    Announce(groundOwner, groundMine, GroundClient);
                    Announce(owner, mine);
                    Announce(crewOwner, crewMine, CrewClient);
                    double now = tick * Dt;
                    Call("EvaluateClient", groundClient, now);
                    Call("EvaluateClient", crewClient, now);
                    float distance = Vector3.Distance(actual, GroundPawnAt);
                    string space = $"ship {(inBox ? "in the box" : "in space")}, alt {actual.y:0} m";
                    // A carrier is a region entity (WorkerInterest.InterestAdd), so a worker sends it only to a
                    // gateway that subscribed its region: being in the set is not enough to receive it.
                    bool shipRegion = Of<HashSet<ulong>>(groundClient, "Regions").Contains(GatewayRegionOf(shipId));
                    bool groundRegion = Of<HashSet<ulong>>(crewClient, "Regions").Contains(GatewayRegionOf(groundId));
                    lap.GroundSeesShip.Observe(tick, phase, Of<HashSet<ulong>>(groundClient, "Visible").Contains(shipId), shipRegion, distance, space, "the ship");
                    lap.CrewSeesGround.Observe(tick, phase, Of<HashSet<ulong>>(crewClient, "Visible").Contains(groundId), groundRegion, distance, space, "the ground pawn");
                    int known = 0;
                    foreach (var chunk in _chunks) if (Of<HashSet<string>>(crewClient, "KnownContainers").Contains(chunk.ContainerId)) known++;
                    if (!inBox && lap.CrewKnownChunksInSpace < 0) lap.CrewKnownChunksInSpace = known;
                    if (inBox && lap.CrewKnownChunksInBox < 0) lap.CrewKnownChunksInBox = known;
                }
            }
            lap.FrameCrossings = W1.Instance.FrameCrossings + W2.Instance.FrameCrossings - crossingsBefore;
            lap.CrossingHandoffs = W1.Instance.CrossingHandoffs + W2.Instance.CrossingHandoffs - handoffsBefore;
            tickMs.Sort();
            lap.MedianTickMs = tickMs[tickMs.Count / 2];
            var landed = Authoritative(shipId, out var finalOwner);
            lap.Add(tick, $"landed in {Where(landed)} on {finalOwner.Id} at planet-local {PlanetLocal(landed)}");
            TestContext.WriteLine($"--- lap, planet {(turning ? "turning at " + TurnDegreesPerSecond + " deg/s" : "still")} ---");
            TestContext.WriteLine(lap.Report());
            return lap;
        }

        private ulong GatewayRegionOf(ulong netId)
        {
            var record = ((IDictionary)Field("_entities"))[netId];
            return (ulong)Call("RegionOf", record);
        }

        // ------------------------------------------------------------------------------------ gateway plumbing

        private object Field(string name) => GatewayType.GetField(name, Flags).GetValue(_gateway);

        private object Call(string name, params object[] args)
        {
            try { return GatewayType.GetMethod(name, Flags).Invoke(_gateway, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }

        private static T Of<T>(object o, string field) => (T)o.GetType().GetField(field).GetValue(o);

        /// <summary>
        /// A gateway in this process with interest set up, a transport that only records, and the lease rows the
        /// control plane would hand it: the planet's carried row (a frame with regions of its own) and each chunk's row
        /// with its placement under the planet. Written as <c>OnControlPlaneChanged</c> writes them.
        /// </summary>
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

            var planetRow = new LeaseInfo
            {
                ContainerId = _planet.Carried.ContainerId, WorkerId = W1.Id, Epoch = 1, State = LeaseState.Active,
                OwnPhysicsFrame = true, FrameInterest = FrameInterestMode.OwnRegions,
            };
            var planetEntry = ContainerOwnershipEntry.Of(planetRow, ContainerRef.DynamicIndex, W1.Index);
            ownership.Add(planetEntry);
            byId[planetRow.ContainerId] = planetEntry;
            frames.Add(planetRow.ContainerId);
            carriers.Add(_planet.NetId);

            for (int i = 0; i < _chunks.Count; i++)
            {
                var chunk = _chunks[i];
                var owner = (i & 1) == 0 ? W1 : W2;
                var row = new LeaseInfo
                {
                    ContainerId = chunk.ContainerId, WorkerId = owner.Id, Epoch = 1, State = LeaseState.Active,
                    HasBounds = true, ParentId = _planet.Carried.ContainerId,
                    Center = Double3.From(new Vector3(-3000f + ChunkWidth * i, PlanetBoxCenter.y, 0f)),
                    BoundsSize = new Vector3(ChunkWidth, PlanetBox.y, PlanetBox.z), Authority = ContainerAuthority.Leased,
                };
                var entry = ContainerOwnershipEntry.Of(row, ContainerRef.RuntimeIndex, owner.Index);
                ownership.Add(entry);
                byId[row.ContainerId] = entry;
            }
        }

        private object WorkerConn(ConformanceMesh.Worker worker)
        {
            var type = GatewayType.GetNestedType("WorkerConn", BindingFlags.NonPublic);
            var conn = Activator.CreateInstance(type, true);
            type.GetField("WorkerId").SetValue(conn, worker.Id);
            type.GetField("Index").SetValue(conn, worker.Index);
            type.GetField("Ready").SetValue(conn, true);
            return conn;
        }

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

        /// <summary>The worker's spawn of <paramref name="entity"/>, as it sends it to a gateway, delivered into the gateway.</summary>
        private void Announce(ConformanceMesh.Worker worker, NetworkIdentity entity, ulong ownerClientId = 0)
        {
            EntitySpawnMsg msg = default;
            worker.Act(() => msg = EntitySpawnMsg.From(entity, new NetworkWriter(256), forGateway: true));
            msg.OwnerClientId = ownerClientId;
            Call("OnEntitySpawn", WorkerConn(worker), msg);
        }

        // ------------------------------------------------------------------------------------ the scenario

        /// <summary>
        /// The workers' half: at every chunk seam, worker seam and box crossing the ship is where the script put it and
        /// moving as the script moves it, the crew member never moves in the ship's frame, the climb out of a chunk the
        /// planet's pose owner does not hold is handed to it for the crossing, and the ship lands where it took off.
        /// With the planet turning, the velocity converted at the box's top carries ω × r (NEB-390, see
        /// <see cref="AFrameTurningSlowlyReadsItsAngularVelocity"/>).
        /// </summary>
        [TestCase(false, TestName = "TheShipFliesTheLapWithoutErrorAtAnySeam(planet still)")]
        [TestCase(true, TestName = "TheShipFliesTheLapWithoutErrorAtAnySeam(planet turning)")]
        public void TheShipFliesTheLapWithoutErrorAtAnySeam(bool turning)
        {
            var lap = Fly(turning, withGateway: false);
            Assert.That(lap.MaxPositionError, Is.LessThan(0.01f), "no position error at any seam: " + lap.MaxPositionErrorWhere);
            Assert.That(lap.MaxCrewError, Is.LessThan(0.01f), "the crew member stays in its seat");
            Assert.AreEqual(0, lap.CrewOffShipTicks, "and in the ship's box");
            Assert.AreEqual(2, lap.FrameCrossings, "out of the planet's frame once and back in once");
            Assert.That(lap.CrossingHandoffs, Is.GreaterThanOrEqualTo(1), "w2 hands the climb to the pose owner for the crossing");
            Assert.That(lap.ChunkSeams, Is.GreaterThanOrEqualTo(2), "two chunk seams crossed at speed 300 m up");
            Assert.That(lap.ShipAltitudeLeavingBox, Is.GreaterThan(600f).And.LessThan(600f + _mesh.Config.HandoverHysteresis + TopSpeed * Dt), "left through the top of the box");
            Assert.That(lap.ShipAltitudeEnteringBox, Is.LessThan(600f).And.GreaterThan(590f), "came back in through the top");
            Assert.That(lap.MaxVelocityError, Is.LessThan(0.05f), $"no velocity error at any seam (the planet turns at {lap.OmegaTruth:0.00000} rad/s, its frame read "
                + $"{lap.OmegaAfterW1Min:0.00000}..{lap.OmegaAfterW1Max:0.00000} rad/s): " + lap.MaxVelocityErrorWhere);
        }

        /// <summary>
        /// The ground pawn's client holds the ship (whose radius is clamped to <c>InterestMaxRadius</c>) until it is that
        /// far away plus the exit margin and linger, and holds it again on the way down once it is back within the radius,
        /// in space above the box. But a carrier is a region entity on its worker (a wide carrier would strand what it
        /// carries), so the gateway only receives it while it subscribes the ship's region: within
        /// <c>InterestRadius</c> + <c>InterestSubscribeMargin</c> of the client, not <c>InterestMaxRadius</c>.
        /// </summary>
        [TestCase(false, TestName = "TheGroundSeesTheShipToInterestMaxRadiusBothWays(planet still)")]
        [TestCase(true, TestName = "TheGroundSeesTheShipToInterestMaxRadiusBothWays(planet turning)")]
        public void TheGroundSeesTheShipToInterestMaxRadiusBothWays(bool turning)
        {
            var lap = Fly(turning, withGateway: true);
            float max = _mesh.Config.InterestMaxRadius;
            var view = lap.GroundSeesShip;
            Assert.That(view.LostAt, Is.GreaterThan(max).And.LessThan(max + 16f + TopSpeed * 1.5f), "out of the set at InterestMaxRadius plus the exit margin and linger");
            Assert.That(view.RegainedAt, Is.GreaterThan(0f).And.LessThanOrEqualTo(max), "and back in it inside the radius on the way down");
            StringAssert.Contains("in space", view.RegainedWhere, "while the ship is still above the planet's box");
            if (view.UnsubscribedAt >= 0f && view.UnsubscribedAt < max)
                Assert.Inconclusive($"Nebula draft (carrier reach): the ship is in the ground client's set out to {view.LostAt:0} m, but a carrier is bucketed by region, and the gateway stopped subscribing the ship's region at {view.UnsubscribedAt:0} m "
                    + $"(InterestRadius {_mesh.Config.InterestRadius} + margins) and took it again at {view.ResubscribedAt:0} m: past that a worker sends it no state.");
        }

        /// <summary>
        /// The crew member's client should hold the ground pawn whenever the ship is within the pawn's radius, inside the
        /// planet's box or above it. Above the box it does so through the focus the gateway adds in the planet's frame while
        /// the pawn is within <c>InterestFrameApproachMargin</c> of its box (NEB-386, <c>docs/container-tree.md</c> D23, scenario 42):
        /// before it, the pawn was lost the moment the ship left the box and came back only when it was inside it again.
        /// </summary>
        [TestCase(false, TestName = "TheCrewSeesTheGroundUntilInterestMaxRadius(planet still)")]
        [TestCase(true, TestName = "TheCrewSeesTheGroundUntilInterestMaxRadius(planet turning)")]
        public void TheCrewSeesTheGroundUntilInterestMaxRadius(bool turning)
        {
            var lap = Fly(turning, withGateway: true);
            float max = _mesh.Config.InterestMaxRadius;
            var view = lap.CrewSeesGround;
            Assert.That(view.Lines.Count, Is.GreaterThanOrEqualTo(1), "the crew client held the ground pawn at the start");
            StringAssert.Contains("in space", view.RegainedWhere, "regained above the box, before the ship is in it again (ship entered the box at " + lap.ShipAltitudeEnteringBox.ToString("0") + " m)");
            Assert.That(view.RegainedAt, Is.GreaterThan(max - 100f), "regained as soon as the ship is back within InterestMaxRadius");
        }

        /// <summary>
        /// A frame's angular velocity (<see cref="PhysicsFrameState.AngularVelocity"/>, D14), for a carrier turning at a
        /// steady rate from a day-long spin to a fast one, sampled once per 60 Hz tick as a worker samples it. A one-tick
        /// difference of float quaternions through <c>ToAngleAxis</c> read exactly zero up to 2 degrees a second, so a
        /// crossing of a turning frame missed ω × r (NEB-390). Read once the frame has a couple of seconds of history,
        /// it is within 1% everywhere.
        /// </summary>
        [Test]
        public void AFrameTurningSlowlyReadsItsAngularVelocity()
        {
            var frame = _planet.Carried.Frame;
            var sb = new StringBuilder();
            bool broken = false;
            uint tick = 1000;
            foreach (float degreesPerSecond in new[] { 360f / 86400f, 360f / 7200f, 0.1f, 0.5f, 1f, 2f, 5f, 30f })
            {
                float truth = degreesPerSecond * Mathf.Deg2Rad;
                float min = float.MaxValue, max = float.MinValue;
                frame.ResetMotion();
                for (int i = 0; i < 300; i++, tick++)
                {
                    _planet.transform.rotation = Quaternion.Euler(0f, 40f + degreesPerSecond * i * Dt, 0f);
                    frame.Sample(tick, Dt);
                    if (i < PhysicsFrame.RotationHistory + 2) continue;
                    min = Mathf.Min(min, frame.State.AngularVelocity.y);
                    max = Mathf.Max(max, frame.State.AngularVelocity.y);
                }
                bool ok = Mathf.Abs(min - truth) <= truth * 0.01f && Mathf.Abs(max - truth) <= truth * 0.01f;
                broken |= !ok;
                sb.AppendLine($"{degreesPerSecond,10:0.#####} deg/s: truth {truth:0.0000000} rad/s, read {min:0.0000000}..{max:0.0000000} rad/s{(ok ? "" : "  <- wrong")}; ω × r at 10 km: {truth * 10000f:0.00} m/s");
            }
            TestContext.WriteLine(sb.ToString());
            Assert.IsFalse(broken, "every rate read within 1%:\n" + sb);
        }

        /// <summary>
        /// A planet the size of Holoverse's (250 km out from its centre) turning once in about 17 hours, ω = 1e-4 rad/s,
        /// about an upright axis and a tilted one, from an arbitrary starting angle: the frame reads ω within 1%, and a
        /// point 250 km out moves at ω × r within 0.1 m/s (25 m/s of it is the spin).
        /// </summary>
        [TestCase(0f, TestName = "AFrameReadsAPlanetsSpinAtHoloverseRates(upright)")]
        [TestCase(23.5f, TestName = "AFrameReadsAPlanetsSpinAtHoloverseRates(tilted)")]
        public void AFrameReadsAPlanetsSpinAtHoloverseRates(float tilt)
        {
            const float Omega = 1e-4f;
            var frame = _planet.Carried.Frame;
            var axis = Quaternion.Euler(tilt, 0f, 0f) * Vector3.up;
            var truth = axis * Omega;
            var surface = new Vector3(250000f, 0f, 0f);
            frame.ResetMotion();
            float worstRelative = 0f, worstPoint = 0f;
            uint tick = 5000;
            for (int i = 0; i < 600; i++, tick++)
            {
                // From an arbitrary angle, as a game turning a planet by a float angle each tick would set it.
                _planet.transform.rotation = Quaternion.AngleAxis(137.3f + Omega * Mathf.Rad2Deg * i * Dt, axis);
                frame.Sample(tick, Dt);
                if (i < PhysicsFrame.RotationHistory + 2) continue;
                var state = frame.State;
                worstRelative = Mathf.Max(worstRelative, (state.AngularVelocity - truth).magnitude / Omega);
                var expected = Vector3.Cross(truth, state.Rotation * surface);
                worstPoint = Mathf.Max(worstPoint, (state.PointVelocity(surface) - expected).magnitude);
            }
            TestContext.WriteLine($"tilt {tilt}: worst ω error {worstRelative * 100f:0.000}%, worst ω × r error 250 km out {worstPoint * 1000f:0.0} mm/s (of {Omega * 250000f:0.0} m/s)");
            Assert.That(worstRelative, Is.LessThan(0.01f), "ω within 1%");
            Assert.That(worstPoint, Is.LessThan(0.1f), "ω × r 250 km out within 0.1 m/s");
        }

        /// <summary>
        /// A frame that stops turning reads it at once: the longer baseline a slow spin is read over is not used once
        /// the newest tick disagrees with it, so a ship that stops yawing does not read a fading spin for a second.
        /// </summary>
        [Test]
        public void AFrameThatStopsTurningReadsStillAtOnce()
        {
            var frame = _planet.Carried.Frame;
            frame.ResetMotion();
            uint tick = 9000;
            float angle = 10f;
            for (int i = 0; i < 200; i++, tick++)
            {
                angle += 0.5f * Dt;
                _planet.transform.rotation = Quaternion.Euler(0f, angle, 0f);
                frame.Sample(tick, Dt);
            }
            Assert.That(frame.State.AngularVelocity.y, Is.EqualTo(0.5f * Mathf.Deg2Rad).Within(0.5f * Mathf.Deg2Rad * 0.01f), "turning at half a degree a second");
            for (int i = 0; i < 3; i++, tick++) frame.Sample(tick, Dt);
            Assert.That(frame.State.AngularVelocity.magnitude, Is.LessThan(1.5e-4f), "still within a tick of stopping");
        }

        /// <summary>
        /// The client's half of the box crossing (the airlock rule: no one-frame change of pose at a frame crossing). A
        /// client copy of the ship, fed exactly what its workers streamed and interpolating two ticks behind, with the
        /// planet's frame posed at the planet's interpolated pose as a client poses it (D11), is drawn where the ship
        /// was at every render step: across both crossings of the planet's box and every chunk and worker seam. The
        /// crew member's render pose is the ship's pose times its seat, so a jump of the ship is a jump of the crew.
        /// </summary>
        [TestCase(false, TestName = "AClientDrawsTheShipAcrossTheBoxWithoutAJump(planet still)")]
        [TestCase(true, TestName = "AClientDrawsTheShipAcrossTheBoxWithoutAJump(planet turning)")]
        public void AClientDrawsTheShipAcrossTheBoxWithoutAJump(bool turning)
        {
            var lap = Fly(turning, withGateway: false);
            Assert.AreEqual(2, lap.CrossingTicks.Count, "two crossings of the box");
            var stream = lap.Stream;

            NebulaRuntime.IsServer = false;
            NebulaRuntime.IsClient = true;
            var go = new GameObject("ship-on-a-client");
            _clientObjects.Add(go);
            var copy = go.AddComponent<NetworkIdentity>();
            go.AddComponent<NetworkTransform>().Interpolate = true;
            copy.Initialize();
            copy.NetId = stream[0].Ship.NetId;
            copy.Epoch = stream[0].Ship.Epoch;
            copy.InvokeSpawn();

            // The planet is a remote entity on the client too: its states, the same ticks as the ship's, fill its own
            // buffer, which is where a client finds the planet's pose at a sample's tick (NEB-392).
            var planetBuffer = _planet.gameObject.AddComponent<RemoteInterpolator>();
            _planet.Interpolator = planetBuffer;

            const double Delay = 2.0;
            const int SubSteps = 4;
            float worst = 0f, worstNearCrossing = 0f, worstJump = 0f;
            string worstAt = "", worstJumpAt = "";
            Vector3 lastShown = default, lastTruth = default;
            bool haveLast = false;
            int compared = 0;
            for (int i = 0; i < stream.Count; i++)
            {
                planetBuffer.Push(stream[i].Tick, stream[i].PlanetPosition, stream[i].PlanetRotation, Vector3.zero);
                if (!copy.ReceiveState(stream[i].Tick, stream[i].Worker, stream[i].Ship)) continue;
                for (int k = 0; k < SubSteps; k++)
                {
                    double render = stream[i].Tick - Delay + (double)k / SubSteps;
                    int at = (int)Math.Floor(render) - (int)stream[0].Tick;
                    if (at < 0 || at + 1 >= stream.Count) continue;
                    float f = (float)(render - Math.Floor(render));
                    // The planet's own copy is interpolated to the same render tick, and its frame posed there.
                    _planet.transform.SetPositionAndRotation(
                        Vector3.Lerp(stream[at].PlanetPosition, stream[at + 1].PlanetPosition, f),
                        Quaternion.Slerp(stream[at].PlanetRotation, stream[at + 1].PlanetRotation, f));
                    PhysicsFrames.PoseForRender();
                    ContainerRegistry.RefreshCaches();
                    copy.RemoteTick(render);
                    var shown = copy.Container != null ? copy.Container.ToWorld(copy.LocalPosition) : copy.LocalPosition;
                    var truth = Vector3.Lerp(stream[at].Truth, stream[at + 1].Truth, f);
                    float error = Vector3.Distance(truth, shown);
                    long renderTick = (long)Math.Floor(render);
                    bool nearCrossing = false;
                    foreach (var c in lap.CrossingTicks) if (Math.Abs(renderTick - c) <= 4) nearCrossing = true;
                    if (error > worst) { worst = error; worstAt = $"render tick {render:0.00} ({(nearCrossing ? "at a crossing" : "elsewhere")}): shown {shown}, truth {truth}, in {Where(copy)}"; }
                    if (nearCrossing) worstNearCrossing = Mathf.Max(worstNearCrossing, error);
                    if (haveLast)
                    {
                        float jump = Mathf.Abs(Vector3.Distance(shown, lastShown) - Vector3.Distance(truth, lastTruth));
                        if (jump > worstJump) { worstJump = jump; worstJumpAt = $"render tick {render:0.00} in {Where(copy)}{(nearCrossing ? " at a crossing" : "")}"; }
                    }
                    lastShown = shown;
                    lastTruth = truth;
                    haveLast = true;
                    compared++;
                }
            }
            TestContext.WriteLine($"client: {compared} render steps; worst error {worst * 1000f:0.0} mm at {worstAt}; worst within 4 ticks of a box crossing {worstNearCrossing * 1000f:0.0} mm; worst one-step jump beyond the ship's own motion {worstJump * 1000f:0.0} mm at {worstJumpAt}");
            Assert.That(compared, Is.GreaterThan(stream.Count * 3), "every render step was compared");
            Assert.That(worst, Is.LessThan(0.25f), "drawn within 25 cm of the ship everywhere: " + worstAt);
            Assert.That(worstJump, Is.LessThan(0.05f), "and no step of the drawn pose jumps by more than 5 cm: " + worstJumpAt);
            Assert.That(worstNearCrossing, Is.LessThan(0.01f), "drawn within a centimetre of the ship at the crossings of the planet's box (NEB-392): " + worstAt);
            Assert.That(worst, Is.LessThan(0.01f), "drawn within a centimetre of the ship everywhere: " + worstAt);
            Assert.That(worstJump, Is.LessThan(0.01f), "and no step of the drawn pose jumps by a centimetre: " + worstJumpAt);
        }

        // ------------------------------------------------------------------------------------ wire precision (NEB-387)

        /// <summary>
        /// Positions travel container-local in float32. What a client gets back for a point at increasing distances from
        /// its container's origin: inside a 2 km chunk, at a 10 km planet radius, and for a carrier placed in a system
        /// container at system distances.
        /// </summary>
        [Test]
        public void WireQuantisationOfContainerLocalPositions()
        {
            var sb = new StringBuilder();
            foreach (double d in new[] { 1000.0, 10000.0, 100000.0, 1e6, 1.5e8, 1.5e11 })
            {
                var entry = new EntityStateEntry { NetId = 1, Epoch = 1, Fields = TransformFields.Position, LocalPosition = new Vector3((float)d, 0f, 0f) };
                var w = new NetworkWriter(64);
                entry.Write(w);
                var back = EntityStateEntry.Read(new NetworkReader(w.ToSegment()));
                double step = (double)Mathf.Abs(BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits((float)d) + 1) - (float)d);
                sb.AppendLine($"{d,14:0} m: float32 step {step,12:0.######} m, round trip {back.LocalPosition.x - d:0.######} m");
                if (d <= 10000.0) Assert.That(step, Is.LessThanOrEqualTo(0.001), "a millimetre or better out to a 10 km planet radius");
            }
            TestContext.WriteLine(sb.ToString());
        }
    }
}
