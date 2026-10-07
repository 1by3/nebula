using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 46 (<c>docs/conformance-suite.md</c>, NEB-393): a crewed carrier crosses a worker seam with
    /// the ghost band on. w1's band gives w2 a ghost of the ship before it crosses, the ship is handed over at the
    /// seam, and then w2's band gives w1 a ghost of it. At every step the crew stays aboard on the worker that holds
    /// them and nothing is set down in the ground, which is what two processes do.
    /// <para>
    /// Tier B on a <see cref="ConformanceMesh"/> with a per-worker dynamic registry: before it, the registry was
    /// process-wide, so w2's ghost of the ship registered a second box under the ship's id, the registry evicted w1's
    /// own, and <see cref="ContainerRegistry.UnregisterDynamic"/> evacuated the crew into the yard. The scenarios that
    /// predate it run with the ghost band off (<c>GhostBandMargin = -1</c>) for that reason.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceCrewedCarrierGhostBandTests
    {
        private ConformanceMesh _mesh;
        private Container _yard, _dock;
        private ushort _shipPrefab, _pawnPrefab;

        private ConformanceMesh.Worker W1 => _mesh[0];
        private ConformanceMesh.Worker W2 => _mesh[1];

        [SetUp]
        public void SetUp()
        {
            _mesh = new ConformanceMesh(2, perWorkerRegistry: true);
            _yard = _mesh.AddStaticContainer("yard", Vector3.zero, new Vector3(64f, 40f, 64f));            // x in [-32, 32]
            _dock = _mesh.AddStaticContainer("dock", new Vector3(64f, 0f, 0f), new Vector3(64f, 40f, 64f)); // x in [32, 96]
            _mesh.SetOwner(_yard, W1);
            _mesh.SetOwner(_dock, W2);

            var ship = new GameObject("ship-prefab");
            ship.AddComponent<NetworkIdentity>();
            var hull = ship.AddComponent<Container>();
            hull.ContainerId = "hull";
            hull.Size = new Vector3(20f, 6f, 20f);
            hull.Center = new Vector3(0f, 2f, 0f);
            ship.AddComponent<NetworkTransform>();
            _shipPrefab = _mesh.RegisterPrefab(ship);
            var pawn = new GameObject("pawn-prefab");
            pawn.AddComponent<NetworkIdentity>();
            _pawnPrefab = _mesh.RegisterPrefab(pawn);
        }

        [TearDown]
        public void TearDown() => _mesh.Dispose();

        private static void AssertAboard(NetworkIdentity crew, NetworkIdentity ship, string where)
        {
            Assert.That(ship.Carried, Is.Not.Null, $"{where}: the ship has its box");
            Assert.That(ship.Carried.IsDynamic, Is.True, $"{where}: the ship's box is registered");
            Assert.That(crew.Container, Is.SameAs(ship.Carried), $"{where}: the crew member is still aboard");
            Assert.That(ship.Carried.Entities, Does.Contain(crew), $"{where}: the box lists the crew member");
        }

        [Test]
        public void TheCrewStaysAboardWhileTheShipIsGhostedAndHandedOverAtASeam()
        {
            var ship = W1.SpawnServerDriven(_shipPrefab, _yard, new Vector3(20f, 0f, 0f), Quaternion.identity);
            NetworkIdentity crew = null;
            W1.Act(() =>
            {
                crew = NetworkPrefabs.Instantiate(_pawnPrefab, ship.transform.position + Vector3.up, Quaternion.identity, ship.transform);
                W1.Instance.SpawnServerDriven(crew, ship.Carried);
            });
            AssertAboard(crew, ship, "spawned");

            // Well inside the yard the band is quiet; at 2 m from the seam it gives w2 a ghost of the ship, which
            // registers a box of w2's own under the ship's id.
            ship.transform.position = new Vector3(30f, 0f, 0f);
            W1.PublishTick(1);
            _mesh.Pump();
            var ghost = W2.Find(ship.NetId);
            Assert.That(ghost, Is.Not.Null, "w1's ghost band gave w2 a copy of the ship");
            Assert.That(ghost.HasAuthority, Is.False);
            Assert.That(ghost.Carried, Is.Not.Null.And.Not.SameAs(ship.Carried), "w2 holds a box of its own for it");
            Assert.That(ghost.Carried.IsDynamic, Is.True);
            AssertAboard(crew, ship, "ghosted");
            Assert.That(ship.Carried.Entities.Count, Is.EqualTo(1));
            Assert.That(_yard.Entities.Contains(crew), Is.False, "the crew was not set down in the yard");

            // Across the seam: the next tick hands the ship, and its crew with it, to w2.
            ship.transform.position = new Vector3(40f, 0f, 0f);
            W1.Tick(2);
            _mesh.Pump();
            var arrived = W2.Find(ship.NetId);
            Assert.That(arrived, Is.Not.Null);
            Assert.That(arrived.HasAuthority, Is.True, "w2 owns the ship now");
            var crewOnW2 = W2.Find(crew.NetId);
            Assert.That(crewOnW2, Is.Not.Null, "the crew member went with it");
            Assert.That(crewOnW2.HasAuthority, Is.True);
            AssertAboard(crewOnW2, arrived, "handed over");
            Assert.That(_dock.Entities.Contains(crewOnW2), Is.False, "the crew was not set down on the dock");

            // w2's band now shows w1 a ghost of the ship it just gave away; the crew stays aboard there too.
            W2.PublishTick(3);
            _mesh.Pump();
            var back = W1.Find(ship.NetId);
            Assert.That(back, Is.Not.Null);
            Assert.That(back.HasAuthority, Is.False, "w1 holds the ship as a ghost");
            Assert.That(_yard.Entities.Contains(W1.Find(crew.NetId)), Is.False, "nothing was set down in the yard");
            AssertAboard(crewOnW2, arrived, "ghosted back");
        }
    }
}
