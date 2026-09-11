using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>Containers carried by entities: registration by net id, nesting, wire references and evacuation on despawn.</summary>
    public class DynamicContainerTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private static readonly NetworkWriter Writer = new NetworkWriter(256);

        private Container MakeStatic(string id, Vector3 position, Vector3 size)
        {
            var go = new GameObject(id);
            go.transform.position = position;
            var c = go.AddComponent<Container>();
            c.ContainerId = id;
            c.Size = size;
            c.Center = new Vector3(0, size.y * 0.5f, 0);
            _objects.Add(go);
            return c;
        }

        /// <summary>A carrier entity (identity + DynamicContainer) with a box of <paramref name="size"/> centred above its origin, registered as if it had spawned.</summary>
        private NetworkIdentity MakeCarrier(string name, ulong netId, Vector3 position, Vector3 size)
        {
            var go = new GameObject(name);
            go.transform.position = position;
            _objects.Add(go);
            var identity = go.AddComponent<NetworkIdentity>();
            var box = go.AddComponent<Container>();
            box.ContainerId = name;
            box.Size = size;
            box.Center = new Vector3(0, size.y * 0.5f, 0);
            go.AddComponent<DynamicContainer>();
            identity.Initialize();
            identity.NetId = netId;
            identity.OwnerWorkerIndex = 7;
            identity.SetContainer(ContainerRegistry.Find(position, box));
            identity.InvokeSpawn(); // DynamicContainer.OnNetworkSpawn registers the box
            return identity;
        }

        private NetworkIdentity MakeEntity(string name, ulong netId, Vector3 position)
        {
            var go = new GameObject(name);
            go.transform.position = position;
            _objects.Add(go);
            var identity = go.AddComponent<NetworkIdentity>();
            identity.Initialize();
            identity.NetId = netId;
            identity.SetContainer(ContainerRegistry.Find(position));
            return identity;
        }

        [SetUp]
        public void SetUp()
        {
            MakeStatic("outdoor", new Vector3(0, 0, 0), new Vector3(200, 60, 200));
            MakeStatic("hut", new Vector3(50, 0, 50), new Vector3(10, 5, 10));
            ContainerRegistry.Rebuild();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) Object.DestroyImmediate(go);
            _objects.Clear();
            ContainerRegistry.Rebuild();
        }

        [Test]
        public void ACarrierRegistersItsContainerByNetIdAndIsNotAStaticContainer()
        {
            var ship = MakeCarrier("ship", 42, new Vector3(-20, 0, 0), new Vector3(10, 6, 20));
            Assert.AreEqual(2, ContainerRegistry.Count, "carried containers are not part of the static, indexed set");
            Assert.AreEqual(1, ContainerRegistry.Dynamic.Count);
            var box = ship.Carried;
            Assert.IsNotNull(box);
            Assert.IsTrue(box.IsDynamic);
            Assert.AreSame(ship, box.Carrier);
            Assert.AreEqual(ContainerRef.DynamicIndex, box.Index);
            Assert.AreEqual("ship#42", box.ContainerId);
            Assert.AreSame(box, ContainerRegistry.Resolve(ContainerRef.Dynamic(42)));
            Assert.AreSame(box, ContainerRegistry.FindDynamic(42));
            Assert.AreEqual("outdoor", ship.Container.ContainerId, "the carrier sits in the container around it, never in its own box");
            Assert.AreEqual(7, box.OwnerWorkerIndex, "ownership is derived from the carrier");
        }

        [Test]
        public void PointsInsideTheCarriedBoxResolveToItAndHysteresisApplies()
        {
            var ship = MakeCarrier("ship", 42, new Vector3(-20, 0, 0), new Vector3(10, 6, 20));
            var box = ship.Carried;
            Assert.AreSame(box, ContainerRegistry.Find(new Vector3(-20, 1, 0)));
            Assert.AreSame(ContainerRegistry.FindById("outdoor"), ContainerRegistry.Find(new Vector3(-40, 1, 0)));
            var outdoor = ContainerRegistry.FindById("outdoor");
            // Walking in through the side: 0.2 m inside is not enough, 0.5 m is.
            Assert.AreSame(outdoor, ContainerRegistry.Resolve(new Vector3(-24.8f, 1, 0), outdoor, 0.35f));
            Assert.AreSame(box, ContainerRegistry.Resolve(new Vector3(-24.5f, 1, 0), outdoor, 0.35f));
            // The carrier's own position is excluded from its own box.
            Assert.AreSame(outdoor, ContainerRegistry.Resolve(ship.transform.position, outdoor, 0.35f, box));
        }

        [Test]
        public void TheCarriedBoxMovesWithTheCarrier()
        {
            var ship = MakeCarrier("ship", 42, new Vector3(-20, 0, 0), new Vector3(10, 6, 20));
            var box = ship.Carried;
            ship.transform.position = new Vector3(60, 0, 0);
            ContainerRegistry.RefreshCaches();
            Assert.AreSame(box, ContainerRegistry.Find(new Vector3(60, 1, 0)));
            Assert.AreNotEqual(box, ContainerRegistry.Find(new Vector3(-20, 1, 0)));
            Assert.AreEqual(new Vector3(1, 1, 2), box.ToLocal(new Vector3(61, 1, 2)));
        }

        [Test]
        public void ContentsAreParentedUnderTheCarrierAndReplicatedInItsSpace()
        {
            var ship = MakeCarrier("ship", 42, new Vector3(-20, 0, 0), new Vector3(10, 6, 20));
            var pawn = MakeEntity("pawn", 43, new Vector3(-18, 0, 3));
            Assert.AreSame(ship.Carried, pawn.Container);
            Assert.AreSame(ship.transform, pawn.transform.parent);
            CollectionAssert.Contains(ship.Carried.Entities, pawn);
            Assert.AreEqual(new Vector3(2, 0, 3), pawn.LocalPosition);
            Assert.IsTrue(pawn.ContainerRef.IsDynamic);
            Assert.AreEqual(42UL, pawn.ContainerRef.NetId);
            // A local pose set through the identity lands under the carrier wherever it is now.
            ship.transform.position = new Vector3(0, 10, 0);
            ship.transform.rotation = Quaternion.Euler(0, 90, 0);
            pawn.SetLocalPose(pawn.Container, new Vector3(2, 0, 3), Quaternion.identity);
            var expected = ship.transform.TransformPoint(new Vector3(2, 0, 3));
            Assert.Less((pawn.transform.position - expected).magnitude, 1e-4f);
        }

        [Test]
        public void NeighboursIncludeTheEnclosingContainerAndTouchingBoxes()
        {
            var ship = MakeCarrier("ship", 42, new Vector3(44, 0, 50), new Vector3(10, 6, 20)); // touches the hut
            var box = ship.Carried;
            var outdoor = ContainerRegistry.FindById("outdoor");
            var hut = ContainerRegistry.FindById("hut");
            var neighbours = new List<Container>();
            ContainerRegistry.NeighborsOf(box, neighbours);
            CollectionAssert.Contains(neighbours, outdoor);
            CollectionAssert.Contains(neighbours, hut);
            ContainerRegistry.NeighborsOf(hut, neighbours);
            CollectionAssert.Contains(neighbours, box);
            CollectionAssert.DoesNotContain(hut.Neighbors, box);
            Assert.IsTrue(outdoor.Encloses(box));
            // Inside the ship, the seam with the outdoor area is the ship's own wall.
            Assert.AreEqual(4f, box.DistanceToSeam(new Vector3(45, 1, 50), outdoor), 0.001f);
        }

        [Test]
        public void UnregisteringEvacuatesTheContentsToTheContainerAround()
        {
            var ship = MakeCarrier("ship", 42, new Vector3(-20, 0, 0), new Vector3(10, 6, 20));
            var pawn = MakeEntity("pawn", 43, new Vector3(-18, 0, 3));
            var worldBefore = pawn.transform.position;
            ship.InvokeDespawn(); // DynamicContainer.OnNetworkDespawn unregisters and evacuates
            Assert.AreEqual(0, ContainerRegistry.Dynamic.Count);
            Assert.IsNull(ContainerRegistry.Resolve(ContainerRef.Dynamic(42)));
            Assert.AreEqual("outdoor", pawn.Container.ContainerId);
            Assert.AreNotSame(ship.transform, pawn.transform.parent);
            Assert.Less((pawn.transform.position - worldBefore).magnitude, 1e-4f, "evacuation keeps the world pose");
        }

        [Test]
        public void NestedCarriersResolveInnermostAndReportDepth()
        {
            var carrier = MakeCarrier("carrier", 42, new Vector3(-20, 0, 0), new Vector3(40, 20, 80));
            var shuttle = MakeCarrier("shuttle", 43, new Vector3(-20, 1, 10), new Vector3(6, 4, 10));
            Assert.AreSame(carrier.Carried, shuttle.Container, "the shuttle sits inside the carrier's box");
            Assert.AreSame(shuttle.Carried, ContainerRegistry.Find(new Vector3(-20, 2, 10)));
            Assert.AreEqual(1, carrier.Carried.NestingDepth);
            Assert.AreEqual(2, shuttle.Carried.NestingDepth);
            Assert.AreSame(carrier.Carried, shuttle.Carried.Enclosing);
        }

        [Test]
        public void ContainerRefRoundTripsBothForms()
        {
            Writer.Reset();
            new ContainerRef(3).Write(Writer);
            ContainerRef.Dynamic(0xABCDEF).Write(Writer);
            ContainerRef.None.Write(Writer);
            var r = new NetworkReader(Writer.ToSegment());
            Assert.AreEqual(new ContainerRef(3), ContainerRef.Read(r));
            var d = ContainerRef.Read(r);
            Assert.IsTrue(d.IsDynamic);
            Assert.AreEqual(0xABCDEFUL, d.NetId);
            Assert.IsTrue(ContainerRef.Read(r).IsNone);
            Assert.AreEqual(0, r.Remaining);
            Assert.LessOrEqual(2 + 10 + 2, Writer.Length);
        }

        [Test]
        public void EntityStateEntryCarriesADynamicReference()
        {
            var entry = new EntityStateEntry { NetId = 9, Epoch = 1, Container = ContainerRef.Dynamic(42), LocalPosition = new Vector3(1, 2, 3), LocalRotation = Quaternion.identity, Velocity = Vector3.zero };
            Writer.Reset();
            entry.Write(Writer);
            Assert.LessOrEqual(Writer.Length, EntityStateEntry.WireSize);
            var back = EntityStateEntry.Read(new NetworkReader(Writer.ToSegment()));
            Assert.AreEqual(ContainerRef.Dynamic(42), back.Container);
            Assert.AreEqual(new Vector3(1, 2, 3), back.LocalPosition);
        }

        [Test]
        public void InterpolatorSamplesInTheContainersSpace()
        {
            var ship = MakeCarrier("ship", 42, new Vector3(-20, 0, 0), new Vector3(10, 6, 20));
            var go = new GameObject("remote");
            _objects.Add(go);
            var interp = go.AddComponent<RemoteInterpolator>();
            interp.Push(10, ship.Carried, new Vector3(1, 0, 0), Quaternion.identity, Vector3.zero);
            interp.Push(12, ship.Carried, new Vector3(3, 0, 0), Quaternion.identity, Vector3.zero);
            Assert.IsTrue(interp.Sample(11, out var container, out var local, out _));
            Assert.AreSame(ship.Carried, container);
            Assert.AreEqual(new Vector3(2, 0, 0), local);
            // The ship moved since the samples arrived: the world pose follows it.
            ship.transform.position = new Vector3(100, 0, 0);
            ContainerRegistry.RefreshCaches();
            Assert.IsTrue(interp.Sample(11, out var world, out _));
            Assert.AreEqual(new Vector3(102, 0, 0), world);
        }
    }
}
