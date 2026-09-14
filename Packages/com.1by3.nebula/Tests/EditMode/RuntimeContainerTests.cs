using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Runtime containers: boxes the game registers while the mesh runs, named by a 64-bit id, leased like baked
    /// containers and mirrored on every process from the control plane's lease rows.
    /// </summary>
    public class RuntimeContainerTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<Container> _registered = new List<Container>();
        private readonly List<Container> _unregistering = new List<Container>();

        private const float Chunk = 64f;

        private static ulong IdOf(int x, int z) => ((ulong)(uint)x << 32) | (uint)z;
        private static Bounds ChunkBounds(int x, int z) => new Bounds(new Vector3((x + 0.5f) * Chunk, 0f, (z + 0.5f) * Chunk), new Vector3(Chunk, 512f, Chunk));

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

        [SetUp]
        public void SetUp()
        {
            ContainerRegistry.Rebuild(); // an empty baked set: the runtime world starts from nothing
            ContainerRegistry.RuntimeRegistered += OnRegistered;
            ContainerRegistry.RuntimeUnregistering += OnUnregistering;
        }

        [TearDown]
        public void TearDown()
        {
            ContainerRegistry.RuntimeRegistered -= OnRegistered;
            ContainerRegistry.RuntimeUnregistering -= OnUnregistering;
            _registered.Clear();
            _unregistering.Clear();
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            ContainerRegistry.Rebuild();
            Assert.AreEqual(0, ContainerRegistry.Runtime.Count, "Load forgets runtime containers");
        }

        private void OnRegistered(Container c) => _registered.Add(c);
        private void OnUnregistering(Container c) => _unregistering.Add(c);

        [Test]
        public void RegisterCreatesAResolvableBoxNamedByItsId()
        {
            var c = ContainerRegistry.RegisterRuntime(IdOf(3, 5), ChunkBounds(3, 5));
            Assert.IsTrue(c.IsRuntime);
            Assert.IsFalse(c.IsDynamic);
            Assert.AreEqual(IdOf(3, 5), c.RuntimeId);
            Assert.AreEqual("rt_" + IdOf(3, 5), c.ContainerId);
            Assert.AreEqual(ContainerRef.RuntimeIndex, c.Index);
            Assert.AreEqual(ContainerRef.Runtime(IdOf(3, 5)), c.Ref);
            Assert.AreSame(c, ContainerRegistry.Resolve(c.Ref));
            Assert.AreSame(c, ContainerRegistry.GetRuntime(IdOf(3, 5)));
            Assert.AreSame(c, ContainerRegistry.FindById(c.ContainerId));
            Assert.IsTrue(ContainerRegistry.IsRuntimeId(c.ContainerId));
            Assert.IsFalse(ContainerRegistry.IsRuntimeId("rt_"));
            Assert.IsFalse(ContainerRegistry.IsRuntimeId("outdoor"));
            Assert.IsFalse(ContainerRegistry.IsDynamicId(c.ContainerId));
            Assert.AreEqual(ChunkBounds(3, 5).center, c.WorldBounds.center);
            Assert.AreEqual(ChunkBounds(3, 5).size, c.WorldBounds.size);
            // The baked set is untouched: no dense index was handed out.
            Assert.AreEqual(0, ContainerRegistry.Count);
            Assert.IsNull(ContainerRegistry.Get(0));
            CollectionAssert.AreEqual(new[] { c }, _registered);
            // Registering the same id again is a no-op that returns the same container.
            Assert.AreSame(c, ContainerRegistry.RegisterRuntime(IdOf(3, 5), ChunkBounds(3, 5)));
            Assert.AreEqual(1, _registered.Count);
        }

        [Test]
        public void UnresolvedRuntimeReferenceIsNullLikeAnUnarrivedCarrier()
        {
            Assert.IsNull(ContainerRegistry.Resolve(ContainerRef.Runtime(999)));
            Assert.IsNull(ContainerRegistry.GetRuntime(999));
            Assert.IsFalse(ContainerRegistry.UnregisterRuntime(999));
        }

        [Test]
        public void AdjacentChunksAreNeighboursAndFindPicksTheRightOne()
        {
            var a = ContainerRegistry.RegisterRuntime(IdOf(0, 0), ChunkBounds(0, 0));
            var b = ContainerRegistry.RegisterRuntime(IdOf(1, 0), ChunkBounds(1, 0));
            var far = ContainerRegistry.RegisterRuntime(IdOf(10, 10), ChunkBounds(10, 10));
            CollectionAssert.AreEquivalent(new[] { b }, a.Neighbors);
            CollectionAssert.AreEquivalent(new[] { a }, b.Neighbors);
            Assert.IsEmpty(far.Neighbors);
            var around = new List<Container>();
            ContainerRegistry.NeighborsOf(a, around);
            CollectionAssert.AreEquivalent(new[] { b }, around);

            Assert.AreSame(a, ContainerRegistry.Find(new Vector3(10, 1, 10)));
            Assert.AreSame(b, ContainerRegistry.Find(new Vector3(100, 1, 10)));
            Assert.AreSame(far, ContainerRegistry.Find(new Vector3(10.5f * Chunk, 1, 10.5f * Chunk)));
            // Outside every box, well away from the nearest bucket neighbourhood: nearest still wins.
            Assert.AreSame(far, ContainerRegistry.Find(new Vector3(30 * Chunk, 1, 30 * Chunk)));
            // Hysteresis at the seam works as between baked containers.
            Assert.AreSame(a, ContainerRegistry.Resolve(new Vector3(Chunk + 0.2f, 1, 10), a, 0.35f));
            Assert.AreSame(b, ContainerRegistry.Resolve(new Vector3(Chunk + 0.5f, 1, 10), a, 0.35f));
        }

        [Test]
        public void AlongVisitsRuntimeBoxesThroughTheHash()
        {
            var a = ContainerRegistry.RegisterRuntime(IdOf(0, 0), ChunkBounds(0, 0));
            var b = ContainerRegistry.RegisterRuntime(IdOf(1, 0), ChunkBounds(1, 0));
            ContainerRegistry.RegisterRuntime(IdOf(0, 5), ChunkBounds(0, 5));
            var hit = new List<Container>();
            ContainerRegistry.Along(new Vector3(10, 1, 10), new Vector3(100, 1, 10), 0f, hit);
            CollectionAssert.AreEquivalent(new[] { a, b }, hit);
        }

        [Test]
        public void RuntimeBoxesNeighbourBakedOnesTheyTouch()
        {
            MakeStatic("arena", new Vector3(-10, 0, 0), new Vector3(20, 10, 20)); // x in [-20, 0]
            ContainerRegistry.Rebuild();
            var arena = ContainerRegistry.FindById("arena");
            var chunk = ContainerRegistry.RegisterRuntime(IdOf(0, 0), ChunkBounds(0, 0)); // x in [0, 64]
            CollectionAssert.Contains(chunk.Neighbors, arena);
            CollectionAssert.Contains(arena.Neighbors, chunk);
            Assert.AreSame(arena, ContainerRegistry.Find(new Vector3(-5, 1, 0)));
            Assert.AreSame(chunk, ContainerRegistry.Find(new Vector3(5, 1, 0)));
            ContainerRegistry.UnregisterRuntime(IdOf(0, 0));
            Assert.IsEmpty(arena.Neighbors, "the baked container drops the adjacency when the runtime box goes");
        }

        [Test]
        public void UnregisterRaisesTheEventEvacuatesEntitiesAndDestroysTheObject()
        {
            var a = ContainerRegistry.RegisterRuntime(IdOf(0, 0), ChunkBounds(0, 0));
            var b = ContainerRegistry.RegisterRuntime(IdOf(1, 0), ChunkBounds(1, 0));
            var go = new GameObject("prop");
            _objects.Add(go);
            var e = go.AddComponent<NetworkIdentity>();
            e.Initialize();
            go.transform.position = new Vector3(Chunk - 1f, 1, 10); // inside a, next to b
            e.SetContainer(a);
            var aObject = a.gameObject;
            Assert.IsTrue(ContainerRegistry.UnregisterRuntime(IdOf(0, 0)));
            CollectionAssert.AreEqual(new[] { a }, _unregistering);
            Assert.AreSame(b, e.Container, "an entity left inside moves to the box around it");
            Assert.IsTrue(aObject == null, "the registry destroys the objects it created");
            Assert.IsNull(ContainerRegistry.GetRuntime(IdOf(0, 0)));
            Assert.IsNull(ContainerRegistry.FindById("rt_" + IdOf(0, 0)));
            Assert.IsEmpty(b.Neighbors);
            Assert.AreEqual(1, ContainerRegistry.Runtime.Count);
        }

        [Test]
        public void UnregisterNeverDestroysTheEntitiesInsideEvenWithNoContainerLeft()
        {
            var a = ContainerRegistry.RegisterRuntime(IdOf(0, 0), ChunkBounds(0, 0));
            var go = new GameObject("prop");
            _objects.Add(go);
            var e = go.AddComponent<NetworkIdentity>();
            e.Initialize();
            go.transform.position = new Vector3(10, 0, 10);
            e.SetContainer(a);
            Assert.AreSame(a.transform, go.transform.parent);
            var stray = new GameObject("stray");
            _objects.Add(stray);
            var s = stray.AddComponent<NetworkIdentity>();
            s.Initialize();
            stray.transform.SetParent(a.transform, true); // parented by hand, never SetContainer'd
            ContainerRegistry.UnregisterRuntime(IdOf(0, 0));
            Assert.IsTrue(go != null && e != null, "the entity survives the box");
            Assert.IsNull(e.Container);
            Assert.IsNull(go.transform.parent, "detached, not destroyed with the box");
            Assert.IsTrue(stray != null && stray.transform.parent == null, "so does anything networked that was merely parented under it");
            Assert.AreEqual(new Vector3(10, 0, 10), go.transform.position);
        }

        [Test]
        public void ANewSessionForgetsRuntimeContainersAndSubscribers()
        {
            var c = ContainerRegistry.RegisterRuntime(IdOf(1, 1), ChunkBounds(1, 1));
            _objects.Add(c.gameObject);
            int fired = 0;
            ContainerRegistry.RuntimeUnregistering += _ => fired++;
            ContainerRegistry.ResetForNewSession();
            Assert.AreEqual(0, ContainerRegistry.Runtime.Count);
            Assert.IsNull(ContainerRegistry.GetRuntime(IdOf(1, 1)));
            Assert.IsTrue(c != null, "the reset touches no object");
            Assert.AreEqual(0, fired, "subscribers of the old session are dropped, not called");
            // The test's own subscriptions went with it; put them back so TearDown's expectations hold.
            ContainerRegistry.RuntimeRegistered += OnRegistered;
            ContainerRegistry.RuntimeUnregistering += OnUnregistering;
        }

        [Test]
        public void LeasesApplyToRuntimeContainersAndWaitForLateOnes()
        {
            string id = ContainerRegistry.RuntimeContainerId(IdOf(2, 2));
            ContainerRegistry.ApplyLease(id, "w2", 2, 4, LeaseState.Active);
            var c = ContainerRegistry.RegisterRuntime(IdOf(2, 2), ChunkBounds(2, 2));
            Assert.AreEqual("w2", c.OwnerWorkerId, "a lease that arrived before the box is applied on registration");
            Assert.AreEqual(2, c.OwnerWorkerIndex);
            Assert.AreEqual(4UL, c.LeaseEpoch);
            Assert.IsTrue(c.IsOwnedBy("w2"));
            ContainerRegistry.ApplyLease(id, "w3", 3, 5, LeaseState.Active);
            Assert.AreEqual("w3", c.OwnerWorkerId);
            ContainerRegistry.ForgetLease(id);
            Assert.AreEqual("", c.OwnerWorkerId);
            Assert.AreEqual(ushort.MaxValue, c.OwnerWorkerIndex);
        }

        [Test]
        public void SyncRuntimeMirrorsTheLeaseRows()
        {
            var leases = new List<LeaseInfo>
            {
                new LeaseInfo { ContainerId = "arena", WorkerId = "w1", State = LeaseState.Active, Epoch = 1 },
                new LeaseInfo { ContainerId = ContainerRegistry.RuntimeContainerId(IdOf(0, 0)), WorkerId = "w1", State = LeaseState.Active, Epoch = 1, HasBounds = true, BoundsCenter = ChunkBounds(0, 0).center, BoundsSize = ChunkBounds(0, 0).size },
                new LeaseInfo { ContainerId = ContainerRegistry.RuntimeContainerId(IdOf(1, 0)), WorkerId = "w2", State = LeaseState.Active, Epoch = 1, HasBounds = true, BoundsCenter = ChunkBounds(1, 0).center, BoundsSize = ChunkBounds(1, 0).size },
            };
            ContainerRegistry.SyncRuntime(leases);
            Assert.AreEqual(2, ContainerRegistry.Runtime.Count);
            Assert.IsNotNull(ContainerRegistry.GetRuntime(IdOf(0, 0)));
            Assert.IsNotNull(ContainerRegistry.GetRuntime(IdOf(1, 0)));
            Assert.AreEqual(ChunkBounds(1, 0).center, ContainerRegistry.GetRuntime(IdOf(1, 0)).WorldBounds.center);
            // Idempotent.
            ContainerRegistry.SyncRuntime(leases);
            Assert.AreEqual(2, ContainerRegistry.Runtime.Count);
            Assert.AreEqual(2, _registered.Count);
            // A row that vanished takes its box with it.
            leases.RemoveAt(2);
            ContainerRegistry.SyncRuntime(leases);
            Assert.AreEqual(1, ContainerRegistry.Runtime.Count);
            Assert.IsNull(ContainerRegistry.GetRuntime(IdOf(1, 0)));
            Assert.AreEqual(1, _unregistering.Count);
        }

        [Test]
        public void LocalControlPlaneCreatesRuntimeRowsAssignedToTheRequester()
        {
            var cp = new LocalControlPlane();
            cp.Connect();
            string id = ContainerRegistry.RuntimeContainerId(IdOf(4, 4));
            cp.EnsureRuntimeContainer(id, ChunkBounds(4, 4), "w1");
            var lease = cp.FindLease(id);
            Assert.IsNotNull(lease);
            Assert.IsTrue(lease.HasBounds);
            Assert.AreEqual(ChunkBounds(4, 4), lease.Bounds);
            Assert.AreEqual("w1", lease.WorkerId);
            Assert.AreEqual(LeaseState.Active, lease.State);
            Assert.AreEqual(1UL, lease.Epoch);
            // First wins: a second worker asking for the same box changes nothing.
            cp.EnsureRuntimeContainer(id, ChunkBounds(4, 4), "w2");
            Assert.AreEqual("w1", cp.FindLease(id).WorkerId);
            Assert.AreEqual(1, cp.Leases.Count);
            // Baked rows have no box.
            cp.EnsureContainer("arena");
            Assert.IsFalse(cp.FindLease("arena").HasBounds);
            // Touching only stamps the row; the owner reads the age before retiring.
            var before = cp.FindLease(id).UpdatedAt;
            cp.FindLease(id).UpdatedAt = before.AddSeconds(-30);
            cp.TouchContainer(id);
            Assert.GreaterOrEqual(cp.FindLease(id).UpdatedAt, before);
            Assert.AreEqual("w1", cp.FindLease(id).WorkerId);
            Assert.AreEqual(1UL, cp.FindLease(id).Epoch);
            // Retiring deletes the row.
            cp.RemoveContainer(id);
            Assert.IsNull(cp.FindLease(id));
        }

        [Test]
        public void RuntimeAssignmentIsStickyAndOnlyOrphansMove()
        {
            var workers = new List<WorkerInfo>
            {
                new WorkerInfo { WorkerId = "w1", WorkerIndex = 1, Status = WorkerStatus.Ready },
                new WorkerInfo { WorkerId = "w2", WorkerIndex = 2, Status = WorkerStatus.Ready },
            };
            var leases = new List<LeaseInfo>
            {
                new LeaseInfo { ContainerId = "rt_1", WorkerId = "w1", State = LeaseState.Active, HasBounds = true },
                new LeaseInfo { ContainerId = "rt_2", WorkerId = "w1", State = LeaseState.Active, HasBounds = true },
                new LeaseInfo { ContainerId = "rt_3", WorkerId = "w1", State = LeaseState.Active, HasBounds = true },
                new LeaseInfo { ContainerId = "rt_4", WorkerId = "w9", State = LeaseState.Active, HasBounds = true }, // owner is gone
                new LeaseInfo { ContainerId = "rt_5", WorkerId = "", State = LeaseState.Orphaned, HasBounds = true },
                new LeaseInfo { ContainerId = "arena", WorkerId = "", State = LeaseState.Orphaned }, // baked: not this policy's business
            };
            var changes = NebulaOrchestrator.ComputeRuntimeAssignment(leases, workers);
            Assert.AreEqual(2, changes.Count, "w1's three containers stay where the game put them even though w2 is idle");
            Assert.IsTrue(changes.All(c => c.Value == "w2"), "orphans go to the least loaded worker");
            CollectionAssert.AreEquivalent(new[] { "rt_4", "rt_5" }, changes.Select(c => c.Key));
            Assert.IsEmpty(NebulaOrchestrator.ComputeRuntimeAssignment(leases, new List<WorkerInfo>()));
        }

        [Test]
        public void RebuildingTheBakedSetIgnoresRuntimeObjects()
        {
            ContainerRegistry.RegisterRuntime(IdOf(0, 0), ChunkBounds(0, 0));
            MakeStatic("arena", Vector3.zero, new Vector3(20, 10, 20));
            ContainerRegistry.Rebuild();
            Assert.AreEqual(1, ContainerRegistry.Count);
            Assert.AreEqual("arena", ContainerRegistry.Get(0).ContainerId);
            Assert.AreEqual(0, ContainerRegistry.Runtime.Count, "a boot-time load forgets runtime containers");
        }

        [Test]
        public void BucketSizeCanChangeWithBoxesRegistered()
        {
            var a = ContainerRegistry.RegisterRuntime(IdOf(0, 0), ChunkBounds(0, 0));
            var b = ContainerRegistry.RegisterRuntime(IdOf(1, 0), ChunkBounds(1, 0));
            float previous = ContainerRegistry.RuntimeBucketSize;
            try
            {
                ContainerRegistry.RuntimeBucketSize = 16f;
                Assert.AreSame(a, ContainerRegistry.Find(new Vector3(10, 1, 10)));
                Assert.AreSame(b, ContainerRegistry.Find(new Vector3(100, 1, 10)));
                var hit = new List<Container>();
                ContainerRegistry.Along(new Vector3(10, 1, 10), new Vector3(100, 1, 10), 0f, hit);
                CollectionAssert.AreEquivalent(new[] { a, b }, hit);
            }
            finally
            {
                ContainerRegistry.RuntimeBucketSize = previous;
            }
        }
    }
}
