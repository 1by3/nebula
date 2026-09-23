using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 15 (<c>docs/conformance-suite.md</c>): two carriers whose interiors overlap never end up
    /// inside each other. The resolver skips every container an entity carries, directly or through a chain of
    /// carriers, and falls back to the next smallest box holding the point; <see cref="NetworkIdentity"/> refuses
    /// a placement from anywhere else that would close a cycle; and a chain that is corrupt anyway reads as "no
    /// scope" instead of walking forever. Before this, twenty ships spawned into one chunk put two of them inside
    /// each other, and the next <see cref="Container.InstanceId"/> overflowed the stack on every tick.
    /// <para>
    /// Tier B — real <see cref="NebulaWorker"/>s on the <see cref="ConformanceMesh"/>, driven one whole tick at a
    /// time, because the cycle was built by the tick's own container resolution and the thing to prove is that
    /// the tick keeps running. Nothing here is pure C#: the rule lives in <see cref="Container"/> and
    /// <see cref="NetworkIdentity"/>. The pure half, the interest index's own refusal of a carrier cycle, is
    /// already pinned by <c>CarriedTransitionTests</c>.
    /// </para>
    /// <para>
    /// The two-worker case has one limit of the tier: the container registry is process-wide, so the receiver's
    /// copy of a carrier re-registers the carrier's box and the sender's copy loses it. Nothing is asserted about
    /// the sender after the handover.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceCarrierCycleTests
    {
        private static readonly Vector3 HullSize = new Vector3(20f, 6f, 20f);

        private ConformanceMesh _mesh;
        private Container _yard;
        private ushort _shipPrefab;

        [SetUp]
        public void SetUp()
        {
            _mesh = new ConformanceMesh(2);
            // One 64 m chunk, the shape of the world the cycle was first seen in.
            _yard = _mesh.AddStaticContainer("yard", Vector3.zero, new Vector3(64f, 40f, 64f));
            _mesh.SetOwner(_yard, _mesh[0]);

            var ship = new GameObject("ship-prefab");
            ship.AddComponent<NetworkIdentity>();
            var hull = ship.AddComponent<Container>();
            hull.ContainerId = "hull";
            hull.Size = HullSize;
            hull.Center = new Vector3(0f, 2f, 0f); // the origin is well inside its own box, clear of the floor face
            ship.AddComponent<DynamicContainer>();
            _shipPrefab = _mesh.RegisterPrefab(ship);
        }

        [TearDown]
        public void TearDown() => _mesh.Dispose();

        private NetworkIdentity SpawnShip(ConformanceMesh.Worker worker, Vector3 position)
        {
            var ship = worker.SpawnServerDriven(_shipPrefab, _yard, position, Quaternion.identity);
            Assume.That(ship.Carried, Is.Not.Null, "the carrier registered its box");
            Assume.That(ship.Container, Is.SameAs(_yard));
            return ship;
        }

        /// <summary>Every walk up the carrier chain ends, and ends in the yard's scope.</summary>
        private void AssertChainResolves(NetworkIdentity ship, int shipCount)
        {
            Assert.IsFalse(ship.Container.IsCarriedBy(ship), $"{ship} rides inside itself");
            Assert.AreSame(_yard, ship.Carried.ScopeRoot, $"{ship}'s carrier chain does not end in the yard");
            Assert.AreEqual(0UL, ship.InstanceId);
            Assert.AreEqual(EntityLocation.PublicScope, ship.ScopeKey);
            Assert.AreEqual(0UL, ship.Carried.InstanceId);
            Assert.AreEqual(EntityLocation.PublicScope, ship.Carried.ScopeKey);
            Assert.That(ship.Carried.NestingDepth, Is.InRange(1, shipCount));
        }

        [Test]
        public void TwoCarriersWithOverlappingInteriorsEndUpOneInsideTheOtherAtMost()
        {
            var w1 = _mesh[0];
            // Each origin is 4 m inside the other's 20 m hull: both boxes claim both ships.
            var a = SpawnShip(w1, new Vector3(0f, 0f, 0f));
            var b = SpawnShip(w1, new Vector3(4f, 0f, 0f));
            ulong ticksBefore = w1.Instance.TickCount;

            for (uint tick = 1; tick <= 5; tick++) w1.Tick(tick);

            Assert.AreEqual(ticksBefore + 5, w1.Instance.TickCount, "every tick ran to the end");
            Assert.AreSame(b.Carried, a.Container, "the first ship resolved into the smallest box holding it: the other hull");
            Assert.AreSame(_yard, b.Container, "the second ship skipped the hull that rides inside it and fell back to the yard");
            Assert.IsTrue(a.HasAuthority && b.HasAuthority, "nothing was handed anywhere: both boxes are the worker's own");
            AssertChainResolves(a, 2);
            AssertChainResolves(b, 2);
            Assert.AreEqual(2, a.Carried.NestingDepth);
            Assert.AreSame(b.transform, a.transform.parent, "the inner ship is parented under the outer one, not the other way round");
        }

        [Test]
        public void TwentyShipsSpawnedIntoOneChunkFormAChainAndNoCycle()
        {
            var w1 = _mesh[0];
            const int count = 20;
            var ships = new List<NetworkIdentity>();
            // All within 8 m of each other, so every hull's interior overlaps every other ship's origin.
            for (int i = 0; i < count; i++) ships.Add(SpawnShip(w1, new Vector3(i % 5 * 2f, 0f, i / 5 * 2f)));

            for (uint tick = 1; tick <= 5; tick++) w1.Tick(tick);

            int inYard = 0;
            foreach (var ship in ships)
            {
                AssertChainResolves(ship, count);
                if (ship.Container == _yard) inYard++;
            }
            Assert.AreEqual(1, inYard, "the ships nest into one chain, so exactly one of them is left standing in the yard");
        }

        [Test]
        public void APlacementThatWouldCloseACycleIsRefusedAtAnyLength()
        {
            var w1 = _mesh[0];
            var a = SpawnShip(w1, new Vector3(0f, 0f, 0f));
            var b = SpawnShip(w1, new Vector3(4f, 0f, 0f));
            var c = SpawnShip(w1, new Vector3(-4f, 0f, 0f));
            // a in b in c, placed directly, as a placement from the wire would be.
            b.SetContainer(c.Carried);
            a.SetContainer(b.Carried);

            // c into a's box would put c inside itself through a and b; into b's box, through b alone.
            c.SetContainer(a.Carried);
            Assert.AreSame(_yard, c.Container, "a cycle through two other carriers is refused");
            c.SetContainer(b.Carried);
            Assert.AreSame(_yard, c.Container, "a cycle through one other carrier is refused");
            c.SetContainer(c.Carried);
            Assert.AreSame(_yard, c.Container, "a carrier still cannot be inside its own box");
            Assert.IsFalse(b.Carried.Entities.Contains(c));

            // The same entity may still be placed anywhere that is not a cycle.
            a.SetContainer(c.Carried);
            Assert.AreSame(c.Carried, a.Container);
            foreach (var ship in new[] { a, b, c }) AssertChainResolves(ship, 3);
        }

        [Test]
        public void ACorruptCarrierChainDegradesAndTheTickHealsIt()
        {
            var w1 = _mesh[0];
            var a = SpawnShip(w1, new Vector3(0f, 0f, 0f));
            var b = SpawnShip(w1, new Vector3(4f, 0f, 0f));
            // Past every check, as corrupt state would be: each ship inside the other.
            a.Container = b.Carried;
            b.Container = a.Carried;

            Assert.AreEqual(0UL, a.InstanceId, "a looping chain has no scope, and reading it ends");
            Assert.AreEqual(EntityLocation.PublicScope, b.ScopeKey);
            Assert.IsNull(a.Carried.ScopeRoot);
            Assert.That(a.Carried.NestingDepth, Is.LessThanOrEqualTo(ContainerRegistry.Dynamic.Count + 2));
            Assert.IsTrue(a.Carried.IsCarriedBy(b), "a loop counts as carried by everything on it");
            Assert.DoesNotThrow(() => _ = a.Carried.WorldBounds, "the frame check above a moving box ends");
            Assert.IsTrue(InstanceScenes.Prepare(a.Carried));

            for (uint tick = 1; tick <= 3; tick++) w1.Tick(tick);

            Assert.AreSame(_yard, a.Container, "the first ship resolved out of the loop, with no hysteresis, into the yard");
            Assert.AreSame(a.Carried, b.Container, "the second one is left inside the first: one level, no loop");
            AssertChainResolves(a, 2);
            AssertChainResolves(b, 2);
        }

        [Test]
        public void ACarrierWithAnOverlappingShipAboardHandsOverWithoutClosingACycle()
        {
            var w1 = _mesh[0];
            var w2 = _mesh[1];
            var a = SpawnShip(w1, new Vector3(0f, 0f, 0f));
            var b = SpawnShip(w1, new Vector3(4f, 0f, 0f));
            w1.Tick(1);
            Assume.That(a.Container, Is.SameAs(b.Carried));

            // The yard's lease moves: the outer ship leaves for w2 and takes the one aboard with it.
            ContainerRegistry.ApplyLease(_yard.ContainerId, w2.Id, w2.Index, 2);
            w1.Tick(2);
            _mesh.Pump();

            var a2 = w2.Find(a.NetId);
            var b2 = w2.Find(b.NetId);
            Assert.IsNotNull(a2);
            Assert.IsNotNull(b2);
            Assert.IsTrue(a2.HasAuthority && b2.HasAuthority, "both ships were handed over, the inner one as the outer one's contents");
            ulong ticksBefore = w2.Instance.TickCount;
            for (uint tick = 3; tick <= 6; tick++) w2.Tick(tick);

            Assert.AreEqual(ticksBefore + 4, w2.Instance.TickCount);
            Assert.AreSame(b2.Carried, a2.Container, "the inner ship is still aboard on the receiver");
            Assert.AreSame(_yard, b2.Container, "and the outer ship did not resolve into the hull riding inside it");
            Assert.IsTrue(a2.HasAuthority && b2.HasAuthority, "nothing bounced back");
            AssertChainResolves(a2, 2);
            AssertChainResolves(b2, 2);
        }
    }
}
