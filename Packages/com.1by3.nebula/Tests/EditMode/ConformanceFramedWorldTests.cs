using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 23 (<c>docs/container-tree.md</c> §5, the acceptance of NEB-264): one scope holding a planet
    /// that is a carrier with a physics frame of its own and regions of its own, eight octants leased to two workers,
    /// and a base leased on its own inside one of them. A ship flies in from space and lands in the other worker's
    /// octant without a scope transfer and without a position error at the crossing; the planet turns without moving
    /// anything on it in its own coordinates or in its region keys; it leaves again through the planet's pose owner; and
    /// a frame's floating origin follows what a worker simulates in it.
    /// <para>
    /// Tier B on the <see cref="ConformanceMesh"/>: the container registry is process-wide, so both workers see the
    /// planet's box and octants as one set, which is what a live mesh gives each worker separately.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceFramedWorldTests
    {
        private ConformanceMesh _mesh;
        private Container _space;
        private ushort _planetPrefab, _shipPrefab, _cratePrefab;
        private NetworkIdentity _planet;
        private readonly List<Container> _octants = new List<Container>();
        private Container _base;

        private ConformanceMesh.Worker W1 => _mesh[0];
        private ConformanceMesh.Worker W2 => _mesh[1];

        private const float PlanetSize = 20000f;
        private static readonly Vector3 PlanetAt = new Vector3(30000f, 0f, 0f);

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
            _mesh = new ConformanceMesh(2);
            _space = _mesh.AddStaticContainer("system", Vector3.zero, new Vector3(200000f, 200000f, 200000f));
            _space.Center = Vector3.zero;
            ContainerRegistry.Rebuild();
            _mesh.SetOwner(_space, W1);

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

            var ship = new GameObject("ship-prefab");
            ship.AddComponent<NetworkIdentity>();
            var hull = ship.AddComponent<Container>();
            hull.ContainerId = "ship";
            hull.Size = new Vector3(10f, 6f, 20f);
            hull.Center = new Vector3(0f, 3f, 0f);
            hull.OwnPhysicsFrame = true;
            ship.AddComponent<NetworkTransform>(); // with the Container on its root, the entity carries the box
            _shipPrefab = _mesh.RegisterPrefab(ship);

            var crate = new GameObject("crate-prefab");
            crate.AddComponent<NetworkIdentity>();
            _cratePrefab = _mesh.RegisterPrefab(crate);

            _planet = W1.SpawnServerDriven(_planetPrefab, _space, PlanetAt, Quaternion.identity);
            // Eight leased octants in the planet's frame, alternating between the two workers, and a base leased on
            // its own (to w1) inside an octant w2 holds.
            for (int i = 0; i < 8; i++)
            {
                float h = PlanetSize / 4f;
                var local = new Vector3((i & 1) != 0 ? h : -h, (i & 2) != 0 ? h : -h, (i & 4) != 0 ? h : -h);
                var octant = ContainerRegistry.RegisterRuntime(1000UL + (ulong)i, ContainerPlacement.Child(_planet.Carried.ContainerId, local, Vector3.one * (PlanetSize / 2f), ContainerAuthority.Leased));
                Assert.IsNotNull(octant);
                var owner = (i & 1) != 0 ? W2 : W1;
                ContainerRegistry.ApplyLease(octant.ContainerId, owner.Id, owner.Index, 1);
                _octants.Add(octant);
            }
            _base = ContainerRegistry.RegisterRuntime(2000, ContainerPlacement.Child(_octants[1].ContainerId, new Vector3(0f, -4000f, 0f), new Vector3(200f, 100f, 200f), ContainerAuthority.Leased));
            ContainerRegistry.ApplyLease(_base.ContainerId, W1.Id, W1.Index, 1);
        }

        [TearDown]
        public void TearDown()
        {
            PhysicsFrames.CrossingPolicy = null;
            _octants.Clear();
            _mesh.Dispose();
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
        }

        [Test]
        public void ThePlanetIsOneFrameWithLeasedOctantsAndALeasedBase()
        {
            var planet = _planet.Carried;
            Assert.IsNotNull(planet.Frame);
            foreach (var octant in _octants)
            {
                Assert.AreSame(planet, octant.Parent);
                Assert.AreSame(planet, octant.Space, "octants live in the planet's frame");
                Assert.IsTrue(octant.IsLeased, "a leased child of a moving parent with its own frame (D20)");
                Assert.IsFalse(octant.AuthorityDemoted);
            }
            Assert.AreEqual(W2.Id, _octants[1].OwnerWorkerId);
            Assert.AreEqual(W1.Id, _base.OwnerWorkerId, "the base is leased on its own, whoever holds its octant");
            Assert.AreEqual(2, _base.Depth - planet.Depth);
        }

        [Test]
        public void AShipFliesInAndLandsInTheOtherWorkersOctantWithoutError()
        {
            // Out in space, 1 km from the planet's surface, on w1.
            var ship = W1.SpawnServerDriven(_shipPrefab, _space, PlanetAt + new Vector3(-PlanetSize / 2f - 1000f, 0f, 0f), Quaternion.identity);
            W1.Tick(1);
            Assert.AreSame(_space, ship.Container);

            // The planet turns; the ship flies into it and comes down over octant 5 (+x, -y, +z), which w2 holds.
            _planet.transform.rotation = Quaternion.Euler(0f, 17f, 0f);
            var target = new Vector3(3000f, -2000f, 4000f); // planet-local
            var world = _planet.transform.TransformPoint(target);
            ship.transform.position = world;
            W1.Tick(2);
            var octant = _octants[5];
            Assert.AreSame(octant, ship.Container, "w1, the planet's pose owner, crossed the ship into the planet's frame");
            Assert.AreEqual(1, W1.Instance.FrameCrossings);
            Assert.IsFalse(ship.HasAuthority, "and handed it to w2, which holds the octant");
            _mesh.Pump();

            var theirs = W2.Find(ship.NetId);
            Assert.IsNotNull(theirs);
            Assert.IsTrue(theirs.HasAuthority);
            Assert.AreSame(octant, theirs.Container);
            var expectedLocal = target - octant.transform.localPosition;
            Assert.That(Vector3.Distance(expectedLocal, theirs.LocalPosition), Is.LessThan(1e-2f), "no scope transfer, no fade, and no position error at the crossing");

            // The planet keeps turning: nothing on it moves in its own coordinates, and its region key stays put.
            var before = theirs.LocalPosition;
            WorkerInterestRegion(theirs, out ulong key, out var framePos);
            _planet.transform.rotation = Quaternion.Euler(0f, 71f, 0f);
            WorkerInterestRegion(theirs, out ulong keyAfter, out var framePosAfter);
            Assert.AreEqual(before, theirs.LocalPosition);
            Assert.AreEqual(key, keyAfter, "regions are keyed in the planet's frame, so its rotation churns nothing (D18)");
            Assert.That(Vector3.Distance(framePos, framePosAfter), Is.LessThan(1e-3f));
            Assert.That(Vector3.Distance(target, framePos), Is.LessThan(1e-2f));
        }

        [Test]
        public void TheShipLeavesThroughThePlanetsPoseOwner()
        {
            var ship = W1.SpawnServerDriven(_shipPrefab, _space, PlanetAt + new Vector3(-PlanetSize / 2f - 1000f, 0f, 0f), Quaternion.identity);
            ship.transform.position = _planet.transform.TransformPoint(new Vector3(3000f, -2000f, 4000f));
            W1.Tick(1);
            _mesh.Pump();
            var theirs = W2.Find(ship.NetId);
            Assume.That(theirs != null && theirs.HasAuthority, Is.True);

            // w2 flies it out through the planet's +x face. w2 does not know the planet's pose exactly (w1 simulates
            // the planet), so it hands the ship to w1 for the crossing.
            theirs.transform.position = new Vector3(PlanetSize / 2f + 300f, -2000f, 4000f) + _planet.Carried.Frame.RootOffset; // frame coordinates, simulation space
            _planet.transform.rotation = Quaternion.Euler(0f, 40f, 0f);
            W2.Tick(2);
            Assert.AreEqual(1, W2.Instance.CrossingHandoffs);
            _mesh.Pump();
            var transfer = _mesh.DeliveredOf(MsgId.AuthorityTransfer, W1.Id);
            Assert.IsTrue(transfer[transfer.Count - 1].Read(AuthorityTransferMsg.Read).Crossing);
            var mine = W1.Find(ship.NetId);
            Assert.IsTrue(mine.HasAuthority);
            var expected = _planet.transform.TransformPoint(new Vector3(PlanetSize / 2f + 300f, -2000f, 4000f));
            W1.Tick(3);
            Assert.AreSame(_space, mine.Container, "out in space again, one scope all along");
            Assert.That(Vector3.Distance(expected, mine.transform.position), Is.LessThan(1e-1f), "converted at the planet's pose of this tick");
        }

        [Test]
        public void AFramesOriginFollowsWhatItsWorkerSimulates()
        {
            // A crate on the surface of octant 0 (w1), 7 km from the planet's centre.
            var octant = _octants[0];
            var local = new Vector3(-7000f, -1000f, -2000f); // planet-local
            NetworkIdentity crate = null;
            W1.Act(() =>
            {
                crate = NetworkPrefabs.Instantiate(_cratePrefab, local, Quaternion.identity, octant.ContentRoot);
                W1.Instance.SpawnServerDriven(crate, octant);
            });
            crate.transform.position = local; // frame coordinates in simulation space, the origin still at the centre
            var frame = _planet.Carried.Frame;
            WorkerInterestRegion(crate, out ulong keyBefore, out _);
            var localBefore = crate.LocalPosition;

            W1.Tick(1);
            Assert.AreNotEqual(Vector3.zero, frame.Origin, "the origin moved to the crate");
            Assert.That(crate.transform.position.magnitude, Is.LessThan(PhysicsFrames.OriginShiftThreshold), "simulated near Unity's origin");
            Assert.AreEqual(localBefore, crate.LocalPosition, "container-local coordinates, and so the wire, did not change");
            Assert.That(Vector3.Distance(local, frame.SimulationToLocal(crate.transform.position)), Is.LessThan(1e-2f));
            WorkerInterestRegion(crate, out ulong keyAfter, out _);
            Assert.AreEqual(keyBefore, keyAfter, "nor did its region");
            Assert.AreSame(octant, crate.Container);
        }

        /// <summary>Crossings of the ship's hull only through its airlock, a 3 m opening in the stern.</summary>
        private sealed class Airlock : IFrameCrossingPolicy
        {
            public int Vetoed;
            public FrameCrossing Decide(NetworkIdentity entity, PhysicsFrame frame, bool leaving, Container from, Container to)
            {
                if (!leaving) return FrameCrossing.Allow;
                var local = frame.SimulationToLocal(entity.transform.position);
                if (local.z < -9f && Mathf.Abs(local.x) < 1.5f) return FrameCrossing.Allow;
                Vetoed++;
                return FrameCrossing.Veto;
            }
        }

        [Test]
        public void ACapitalShipAtOneKilometrePerSecondWithALeasedInterior()
        {
            // A capital ship in space, simulated by w1, with its engine room leased to w2 (D20).
            var ship = W1.SpawnServerDriven(_shipPrefab, _space, new Vector3(-50000f, 0f, 0f), Quaternion.Euler(0f, 90f, 0f));
            var hull = ship.Carried;
            var engine = ContainerRegistry.RegisterRuntime(3000, ContainerPlacement.Child(hull.ContainerId, new Vector3(0f, 3f, -6f), new Vector3(10f, 6f, 8f), ContainerAuthority.Leased));
            ContainerRegistry.ApplyLease(engine.ContainerId, W2.Id, W2.Index, 1);
            Assert.IsTrue(engine.IsLeased);
            var airlock = new Airlock();
            PhysicsFrames.CrossingPolicy = airlock;

            NetworkIdentity crew = null;
            W1.Act(() =>
            {
                crew = NetworkPrefabs.Instantiate(_cratePrefab, new Vector3(0f, 1f, 6f), Quaternion.identity, hull.ContentRoot);
                W1.Instance.SpawnServerDriven(crew, hull);
            });
            crew.transform.position = hull.Frame.LocalToSimulation(new Vector3(0f, 1f, 6f));
            float dt = NetworkTime.TickInterval;
            var velocity = new Vector3(1000f, 0f, 0f); // 1 km/s

            // Walk aft from the bridge (w1) into the engine room (w2) while the ship flies.
            uint tick = 1;
            for (float z = 6f; z >= -6f; z -= 1f, tick++)
            {
                ship.transform.position += velocity * dt;
                crew.transform.position = hull.Frame.LocalToSimulation(new Vector3(0f, 1f, z));
                W1.Tick(tick);
                if (!crew.HasAuthority) break;
            }
            Assert.IsFalse(crew.HasAuthority, "handed to w2 at the engine room's seam");
            _mesh.Pump();
            var theirs = W2.Find(crew.NetId);
            Assert.IsTrue(theirs.HasAuthority);
            Assert.AreSame(engine, theirs.Container);
            var frameLocal = hull.Frame.SimulationToLocal(theirs.transform.position);
            Assert.That(Mathf.Abs(frameLocal.x) + Mathf.Abs(frameLocal.y - 1f), Is.LessThan(1e-3f), "no lag error: both workers use the ship's still frame");

            // w2 simulates the crew member while the ship keeps flying; its frame coordinates do not care.
            for (int i = 0; i < 5; i++, tick++)
            {
                ship.transform.position += velocity * dt;
                W2.Tick(tick);
            }
            Assert.That(Vector3.Distance(frameLocal, hull.Frame.SimulationToLocal(theirs.transform.position)), Is.LessThan(1e-3f));

            // Through the side wall: vetoed, it stays aboard. w2 is not the pose owner, so it asks w1 first.
            theirs.transform.position = hull.Frame.LocalToSimulation(new Vector3(6f, 1f, -6f));
            W2.Tick(tick++);
            _mesh.Pump();
            var mine = W1.Find(crew.NetId);
            Assert.IsTrue(mine.HasAuthority, "w2 handed it to the pose owner for the crossing");
            W1.Tick(tick++);
            Assert.GreaterOrEqual(airlock.Vetoed, 1);
            Assert.AreNotSame(_space, mine.Container, "the hull wall is not an exit");

            // Out through the airlock at the stern: allowed, and exact at this tick's pose.
            ship.transform.position += velocity * dt;
            mine.transform.position = hull.Frame.LocalToSimulation(new Vector3(0f, 1f, -10.6f));
            var expected = ship.transform.TransformPoint(new Vector3(0f, 1f, -10.6f));
            W1.Tick(tick++);
            if (!mine.HasAuthority) { _mesh.Pump(); mine = W1.Find(crew.NetId); W1.Tick(tick++); }
            Assert.AreSame(_space, mine.Container, "out through the airlock");
            Assert.That(Vector3.Distance(expected, mine.transform.position), Is.LessThan(1e-2f), "the crossing is exact");
            Assert.That(mine.Motion.Velocity.x, Is.EqualTo(1000f).Within(5f), "and it keeps the ship's velocity");
        }

        /// <summary>The region key a worker files an entity under, and its position in that key's space (frame-local).</summary>
        private static void WorkerInterestRegion(NetworkIdentity e, out ulong key, out Vector3 framePosition)
        {
            var position = NebulaWorker.RegionSpaceOf(e, out var space);
            framePosition = space != null && space.Frame != null ? space.Frame.SimulationToLocal(position) : position;
            var grid = InterestGrid.Resolve(InterestSettings.Default);
            key = RegionKeys.Salt(grid.RegionOf(framePosition.x, framePosition.y, framePosition.z), e.InstanceId, space != null ? RegionKeys.FrameKeyOf(space.Ref) : 0UL);
        }
    }
}
