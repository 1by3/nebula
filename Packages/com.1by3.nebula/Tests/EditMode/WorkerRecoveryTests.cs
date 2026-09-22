using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    public class WorkerRecoveryTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        [SetUp]
        public void SetUp() => ContainerRegistry.Rebuild();

        [TearDown]
        public void TearDown() => ContainerRegistry.Rebuild();

        [Test]
        public void RuntimeContainerAndOccupantSurvivePendingRecoveryThenRetireNormally()
        {
            using var plane = new LocalControlPlane();
            plane.Connect();
            var registration = new WorkerRegistration { WorkerId = "w1", WorkerIndex = 1 };
            registration.Register(plane);
            string containerId = ContainerRegistry.RuntimeContainerId(42);
            plane.EnsureRuntimeContainer(containerId, new Bounds(Vector3.zero, Vector3.one * 8), "w1");
            ContainerRegistry.SyncRuntime(plane.Leases);
            ContainerRegistry.ApplyLease(containerId, "w1", 1, 1, LeaseState.Active);
            var original = ContainerRegistry.FindById(containerId);
            var containerObject = original.gameObject;
            var occupantObject = new GameObject("recovery occupant");
            var occupant = occupantObject.AddComponent<NetworkIdentity>();
            occupant.Initialize();
            occupant.NetId = 1;
            occupant.Epoch = 1;
            occupant.SetContainer(original);
            occupant.InvokeSpawn();
            int unloads = 0;
            void OnUnregister(Container container) { if (ReferenceEquals(container, original)) unloads++; }
            ContainerRegistry.RuntimeUnregistering += OnUnregister;
            // Keep the production mirror's queue, but drive snapshot arrival explicitly instead of starting
            // its HTTP threads. This deterministically holds the recovery write outside the read snapshot.
            using var remote = new RemoteControlPlane("http://127.0.0.1:1");
            typeof(RemoteControlPlane).GetField("_running", Hidden).SetValue(remote, true);
            try
            {
                plane.ResetControlPlane();
                DeliverSnapshot(remote, plane);
                Assert.That(registration.ReclaimContainers(remote), Is.EqualTo(1));
                Assert.That(remote.PendingWrites, Is.EqualTo(1));
                Assert.That(remote.Leases, Is.Empty);
                registration.SyncRuntime(remote);

                plane.SetSetting("unrelated", "change");
                DeliverSnapshot(remote, plane);
                Assert.That(registration.ReclaimContainers(remote), Is.Zero);
                registration.SyncRuntime(remote);
                Assert.That(ContainerRegistry.FindById(containerId), Is.SameAs(original));
                Assert.That(original.gameObject, Is.SameAs(containerObject));
                Assert.That(occupant.Container, Is.SameAs(original));
                Assert.That(occupant.IsSpawned, Is.True);
                Assert.That(unloads, Is.Zero, "pending recovery must not unload the scene or evacuate occupants");
                Assert.That(remote.PendingWrites, Is.EqualTo(1), "unrelated changes must not duplicate the recovery write");

                var writes = (Queue<string>)typeof(RemoteControlPlane).GetField("_writes", Hidden).GetValue(remote);
                Assert.That(ControlPlaneJson.ApplyBatch(ControlPlaneJson.WriteBatch(writes.ToArray()), plane), Is.Null);
                writes.Clear();
                DeliverSnapshot(remote, plane);
                registration.ReclaimContainers(remote);
                registration.SyncRuntime(remote);
                Assert.That(ContainerRegistry.FindById(containerId), Is.SameAs(original));
                Assert.That(occupant.Container, Is.SameAs(original));
                Assert.That(unloads, Is.Zero);

                plane.RemoveContainer(containerId);
                DeliverSnapshot(remote, plane);
                Assert.That(registration.ReclaimContainers(remote), Is.Zero, "same-document retirement is intentional");
                registration.SyncRuntime(remote);
                Assert.That(ContainerRegistry.FindById(containerId), Is.Null);
                Assert.That(unloads, Is.EqualTo(1));
                Assert.That(containerObject == null, Is.True, "the actual Unity runtime object was retired");
                Assert.That(ReferenceEquals(occupant.Container, original), Is.False, "retirement evacuates the occupant");
            }
            finally
            {
                ContainerRegistry.RuntimeUnregistering -= OnUnregister;
                if (occupantObject != null) Object.DestroyImmediate(occupantObject);
            }
        }

        private static void DeliverSnapshot(RemoteControlPlane remote, LocalControlPlane plane)
        {
            typeof(RemoteControlPlane).GetField("_incoming", Hidden).SetValue(remote, ControlPlaneJson.Parse(plane.ToJson()));
            typeof(RemoteControlPlane).GetField("_lastReadAt", Hidden).SetValue(remote, double.PositiveInfinity);
            remote.Tick();
        }
    }
}
