using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>Leases on carried containers: they follow the carrier unless pinned, and a lease that arrives before the carrier waits for it.</summary>
    public class DynamicContainerLeaseTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        private NetworkIdentity MakeCarrier(string name, ulong netId, ushort ownerIndex)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            var identity = go.AddComponent<NetworkIdentity>();
            var box = go.AddComponent<Container>();
            box.ContainerId = name;
            box.Size = new Vector3(10, 6, 20);
            box.Center = new Vector3(0, 3, 0);
            go.AddComponent<DynamicContainer>();
            identity.Initialize();
            identity.NetId = netId;
            identity.OwnerWorkerIndex = ownerIndex;
            identity.InvokeSpawn();
            return identity;
        }

        [SetUp]
        public void SetUp()
        {
            var go = new GameObject("outdoor");
            _objects.Add(go);
            var c = go.AddComponent<Container>();
            c.ContainerId = "outdoor";
            c.Size = new Vector3(200, 60, 200);
            c.Center = new Vector3(0, 30, 0);
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
        public void DynamicIdsAreRecognisedAndResolvedById()
        {
            var ship = MakeCarrier("ship", 42, 3);
            Assert.IsTrue(ContainerRegistry.IsDynamicId("ship#42"));
            Assert.IsFalse(ContainerRegistry.IsDynamicId("outdoor"));
            Assert.AreEqual(42UL, ContainerRegistry.CarrierNetIdOf("ship#42"));
            Assert.AreSame(ship.Carried, ContainerRegistry.FindById("ship#42"));
        }

        [Test]
        public void AnActiveLeaseIsInformationalAndAPinnedOneTakesOver()
        {
            var ship = MakeCarrier("ship", 42, 3);
            var box = ship.Carried;
            ContainerRegistry.ApplyLease("ship#42", "w3", 3, 5, LeaseState.Active);
            Assert.IsFalse(box.IsPinned);
            Assert.AreEqual(3, box.OwnerWorkerIndex, "an active lease on a carried container just mirrors the carrier");
            Assert.AreEqual(ship.Epoch, box.LeaseEpoch);

            ContainerRegistry.ApplyLease("ship#42", "w7", 7, 6, LeaseState.Pinned);
            Assert.IsTrue(box.IsPinned);
            Assert.AreEqual(7, box.OwnerWorkerIndex);
            Assert.AreEqual("w7", box.OwnerWorkerId);
            Assert.AreEqual(6UL, box.LeaseEpoch);
            Assert.IsTrue(box.IsOwnedBy("w7"));

            ContainerRegistry.ApplyLease("ship#42", "w7", 7, 6, LeaseState.Active);
            Assert.IsFalse(box.IsPinned);
            Assert.AreEqual(3, box.OwnerWorkerIndex, "unpinned: back to the carrier's worker");

            ContainerRegistry.ForgetLease("ship#42");
            Assert.AreEqual("", box.LeaseState);
            Assert.AreEqual(3, box.OwnerWorkerIndex);
        }

        [Test]
        public void ALeaseThatArrivesBeforeTheCarrierIsAppliedOnRegistration()
        {
            ContainerRegistry.ApplyLease("ship#42", "w7", 7, 2, LeaseState.Pinned);
            Assert.IsNull(ContainerRegistry.FindById("ship#42"));
            var ship = MakeCarrier("ship", 42, 3);
            Assert.IsTrue(ship.Carried.IsPinned);
            Assert.AreEqual(7, ship.Carried.OwnerWorkerIndex);
            ship.InvokeDespawn();
            // Gone with the carrier: a new life of the same id starts clean.
            var again = MakeCarrier("ship", 42, 3);
            Assert.IsFalse(again.Carried.IsPinned);
        }

        [Test]
        public void LocalControlPlaneRemovesLeaseRows()
        {
            var cp = new LocalControlPlane();
            cp.Connect();
            cp.EnsureContainer("ship#42");
            cp.AssignContainer("ship#42", "w1");
            Assert.IsNotNull(cp.FindLease("ship#42"));
            cp.RemoveContainer("ship#42");
            Assert.IsNull(cp.FindLease("ship#42"));
            Assert.IsTrue(LeaseState.IsOwning(LeaseState.Pinned));
            Assert.IsFalse(LeaseState.IsOwning(LeaseState.Orphaned));
        }
    }
}
