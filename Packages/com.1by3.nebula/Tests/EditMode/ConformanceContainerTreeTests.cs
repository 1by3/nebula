using System.Collections.Generic;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenarios 19 and 21 (<c>docs/container-tree.md</c> §7): one container concept in a tree. Baked
    /// boxes build their tree from nesting; runtime boxes name their parent and carry a parent-local box; resolution
    /// prefers the deepest box; inherited containers take their owner from their parent; a leased box under a moving
    /// parent without a frame of its own is demoted; nothing is placed inside its own subtree; and a root far from
    /// the origin is placed exactly.
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceContainerTreeTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        private Container MakeStatic(string id, Vector3 position, Vector3 size, ContainerAuthority authority = ContainerAuthority.Auto)
        {
            var go = new GameObject(id);
            go.transform.position = position;
            var c = go.AddComponent<Container>();
            c.ContainerId = id;
            c.Size = size;
            c.Center = new Vector3(0, size.y * 0.5f, 0);
            c.Authority = authority;
            _objects.Add(go);
            return c;
        }

        private NetworkIdentity MakeCarrier(string name, ulong netId, Vector3 position, Vector3 size, ushort ownerIndex = 7)
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
            identity.OwnerWorkerIndex = ownerIndex;
            identity.SetContainer(ContainerRegistry.Find(position, box));
            identity.InvokeSpawn();
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

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            ContainerRegistry.Rebuild();
            Assert.AreEqual(0, ContainerRegistry.PendingRuntimeCount, "Load forgets held rows");
        }

        // ------------------------------------------------------------------------------------ baked tree

        [Test]
        public void BakedBoxesBuildTheirTreeFromNesting()
        {
            var outdoor = MakeStatic("outdoor", Vector3.zero, new Vector3(200, 60, 200));
            var hall = MakeStatic("hall", new Vector3(50, 0, 50), new Vector3(40, 20, 40));
            var room = MakeStatic("room", new Vector3(50, 0, 50), new Vector3(10, 5, 10));
            var shed = MakeStatic("shed", new Vector3(-50, 0, -50), new Vector3(10, 5, 10));
            ContainerRegistry.Rebuild();

            Assert.IsNull(outdoor.Parent);
            Assert.AreSame(outdoor, hall.Parent);
            Assert.AreSame(hall, room.Parent, "the smallest enclosing box is the parent");
            Assert.AreSame(outdoor, shed.Parent);
            Assert.AreEqual(0, outdoor.Depth);
            Assert.AreEqual(2, room.Depth);
            CollectionAssert.AreEquivalent(new[] { hall, shed }, outdoor.Children);
            Assert.AreSame(outdoor, room.ScopeRoot);
            Assert.AreEqual(ContainerSource.Baked, room.Source);
            Assert.AreEqual(ContainerFrameMode.Fixed, room.FrameMode);
            Assert.IsTrue(room.IsLeased, "Auto on a fixed box is leased: what baked containers always were");
        }

        [Test]
        public void AnInheritedBakedBoxTakesItsOwnerFromItsParent()
        {
            var outdoor = MakeStatic("outdoor", Vector3.zero, new Vector3(200, 60, 200));
            var hall = MakeStatic("hall", new Vector3(50, 0, 50), new Vector3(40, 20, 40));
            var room = MakeStatic("room", new Vector3(50, 0, 50), new Vector3(10, 5, 10), ContainerAuthority.Inherited);
            ContainerRegistry.Rebuild();
            ContainerRegistry.ApplyLease("outdoor", "w1", 1, 3);
            ContainerRegistry.ApplyLease("hall", "w2", 2, 5);
            ContainerRegistry.ApplyLease("room", "w9", 9, 1); // a stray row for an inherited box is ignored

            Assert.IsFalse(room.IsLeased);
            Assert.AreEqual("w2", room.OwnerWorkerId);
            Assert.AreEqual(2, room.OwnerWorkerIndex);
            Assert.AreEqual(5UL, room.LeaseEpoch);

            ContainerRegistry.ApplyLease("hall", "w3", 3, 6); // re-dealing the parent moves the child with it
            Assert.AreEqual("w3", room.OwnerWorkerId);
            Assert.AreEqual(6UL, room.LeaseEpoch);
        }

        // ------------------------------------------------------------------------------------ runtime children

        [Test]
        public void ARuntimeChildIsPlacedInItsParentsFrameAndMovesWithIt()
        {
            ContainerRegistry.Rebuild();
            var planet = ContainerRegistry.RegisterRuntime(1, ContainerPlacement.Root(new Double3(1000, 0, 0), new Vector3(400, 400, 400)));
            var octant = ContainerRegistry.RegisterRuntime(2, ContainerPlacement.Child(planet.ContainerId, new Vector3(100, 100, 100), new Vector3(200, 200, 200)));

            Assert.AreSame(planet, octant.Parent);
            Assert.AreEqual(1, octant.Depth);
            CollectionAssert.Contains(planet.Children, octant);
            Assert.AreEqual(new Vector3(1100, 100, 100), octant.WorldBounds.center);
            Assert.AreSame(octant, ContainerRegistry.Find(new Vector3(1150, 150, 150)));
            Assert.AreSame(planet, ContainerRegistry.Find(new Vector3(950, -150, 150)));

            var placement = ContainerRegistry.PlacementOf(octant);
            Assert.AreEqual(planet.ContainerId, placement.ParentId);
            Assert.AreEqual(new Double3(100, 100, 100), placement.Center);
        }

        [Test]
        public void AChildWhoseParentIsNotHereYetIsHeldUntilItArrives()
        {
            ContainerRegistry.Rebuild();
            var early = ContainerRegistry.RegisterRuntime(20, ContainerPlacement.Child(ContainerRegistry.RuntimeContainerId(10), new Vector3(5, 0, 0), new Vector3(4, 4, 4)));
            Assert.IsNull(early);
            Assert.AreEqual(1, ContainerRegistry.PendingRuntimeCount);

            var parent = ContainerRegistry.RegisterRuntime(10, ContainerPlacement.Root(new Double3(0, 0, 0), new Vector3(40, 40, 40)));
            Assert.AreEqual(0, ContainerRegistry.PendingRuntimeCount);
            var child = ContainerRegistry.GetRuntime(20);
            Assert.IsNotNull(child);
            Assert.AreSame(parent, child.Parent);

            // The parent goes: the child waits for it again, and comes back with it.
            ContainerRegistry.UnregisterRuntime(10);
            Assert.IsNull(ContainerRegistry.GetRuntime(20));
            Assert.AreEqual(1, ContainerRegistry.PendingRuntimeCount);
            ContainerRegistry.RegisterRuntime(10, ContainerPlacement.Root(new Double3(0, 0, 0), new Vector3(40, 40, 40)));
            Assert.IsNotNull(ContainerRegistry.GetRuntime(20));
        }

        [Test]
        public void SyncRuntimeRegistersRowsInAnyOrder()
        {
            ContainerRegistry.Rebuild();
            var leases = new List<LeaseInfo>
            {
                new LeaseInfo { ContainerId = "rt_3", HasBounds = true, ParentId = "rt_2", Center = new Double3(1, 0, 0), BoundsSize = Vector3.one },
                new LeaseInfo { ContainerId = "rt_2", HasBounds = true, ParentId = "rt_1", Center = new Double3(10, 0, 0), BoundsSize = Vector3.one * 5 },
                new LeaseInfo { ContainerId = "rt_1", HasBounds = true, Center = new Double3(100, 0, 0), BoundsSize = Vector3.one * 50 },
            };
            ContainerRegistry.SyncRuntime(leases);
            Assert.AreEqual(3, ContainerRegistry.Runtime.Count);
            Assert.AreEqual(2, ContainerRegistry.GetRuntime(3).Depth);
            Assert.AreEqual(new Vector3(111, 0, 0), ContainerRegistry.GetRuntime(3).transform.position);

            leases.RemoveAt(2); // the root's row is gone: the whole branch goes with it
            ContainerRegistry.SyncRuntime(leases);
            Assert.AreEqual(0, ContainerRegistry.Runtime.Count);
        }

        [Test]
        public void TheDeepestBoxWinsOverTheSmallest()
        {
            var room = MakeStatic("room", Vector3.zero, new Vector3(10, 6, 10));
            ContainerRegistry.Rebuild();
            // A big box registered as a child of the room: deeper than the room though larger where they overlap.
            var nook = ContainerRegistry.RegisterRuntime(5, ContainerPlacement.Child("room", new Vector3(5, 3, 0), new Vector3(20, 6, 4)));
            Assert.AreSame(room, nook.Parent);
            Assert.AreSame(nook, ContainerRegistry.Find(new Vector3(4, 3, 0)), "depth first, volume only breaks ties");
        }

        [Test]
        public void AnInheritedRuntimeChildFollowsItsParentsLease()
        {
            ContainerRegistry.Rebuild();
            var octant = ContainerRegistry.RegisterRuntime(1, ContainerPlacement.Root(Double3.Zero, new Vector3(100, 100, 100)));
            var baseBox = ContainerRegistry.RegisterRuntime(2, ContainerPlacement.Child(octant.ContainerId, Vector3.zero, new Vector3(10, 10, 10), ContainerAuthority.Inherited));
            ContainerRegistry.ApplyLease(octant.ContainerId, "w1", 1, 4);
            Assert.AreEqual("w1", baseBox.OwnerWorkerId);
            ContainerRegistry.ApplyLease(octant.ContainerId, "w2", 2, 5);
            Assert.AreEqual("w2", baseBox.OwnerWorkerId, "re-dealing the octant moves the base with it");

            // The same base leased on its own: its own row decides.
            var leased = ContainerRegistry.RegisterRuntime(3, ContainerPlacement.Child(octant.ContainerId, new Vector3(20, 0, 0), new Vector3(10, 10, 10), ContainerAuthority.Leased));
            ContainerRegistry.ApplyLease(leased.ContainerId, "w3", 3, 1);
            Assert.IsTrue(leased.IsLeased);
            Assert.AreEqual("w3", leased.OwnerWorkerId);
        }

        // ------------------------------------------------------------------------------------ the rule (D7)

        [Test]
        public void ALeasedChildOfAMovingParentWithoutAFrameIsDemoted()
        {
            MakeStatic("outdoor", Vector3.zero, new Vector3(400, 60, 400));
            ContainerRegistry.Rebuild();
            var ship = MakeCarrier("ship", 42, new Vector3(20, 0, 0), new Vector3(20, 10, 40), ownerIndex: 1);
            ContainerRegistry.WorkerIdByIndex = i => "w" + i;
            try
            {
                UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("simulated as inherited"));
                var engineRoom = ContainerRegistry.RegisterRuntime(7, ContainerPlacement.Child(ship.Carried.ContainerId, new Vector3(0, 3, -10), new Vector3(8, 6, 8), ContainerAuthority.Leased));
                ContainerRegistry.ApplyLease(engineRoom.ContainerId, "w2", 2, 1);
                Assert.IsTrue(engineRoom.AuthorityDemoted);
                Assert.IsTrue(engineRoom.InMovingSpace);
                Assert.IsFalse(engineRoom.IsLeased);
                Assert.AreEqual("w1", engineRoom.OwnerWorkerId, "simulated by the hull's owner instead");

                // With a frame of its own on the ship the same child is leased (D20).
                ship.Carried.OwnPhysicsFrame = true;
                Assert.IsFalse(engineRoom.AuthorityDemoted);
                Assert.IsTrue(engineRoom.IsLeased);
                Assert.AreEqual("w2", engineRoom.OwnerWorkerId);
            }
            finally
            {
                ship.Carried.OwnPhysicsFrame = false;
                ContainerRegistry.WorkerIdByIndex = index => "";
            }
        }

        [Test]
        public void ARoomFixedInAShipMovesWithItAndTicksAfterIt()
        {
            MakeStatic("outdoor", Vector3.zero, new Vector3(400, 60, 400));
            ContainerRegistry.Rebuild();
            var ship = MakeCarrier("ship", 42, new Vector3(20, 0, 0), new Vector3(20, 10, 40));
            var cabin = ContainerRegistry.RegisterRuntime(8, ContainerPlacement.Child(ship.Carried.ContainerId, new Vector3(0, 3, 10), new Vector3(6, 6, 6)));
            Assert.AreEqual(1, cabin.NestingDepth, "one carrier above it");
            Assert.IsTrue(cabin.MayMove);
            Assert.AreSame(cabin, ContainerRegistry.Find(new Vector3(20, 3, 10)));

            ship.transform.position = new Vector3(100, 0, 0);
            ContainerRegistry.RefreshCaches();
            Assert.AreSame(cabin, ContainerRegistry.Find(new Vector3(100, 3, 10)), "the room went with the ship");
            Assert.AreSame(ship.Carried, cabin.Parent);
        }

        // ------------------------------------------------------------------------------------ subtree rule (D4)

        [Test]
        public void NothingIsPlacedInsideItsOwnSubtree()
        {
            MakeStatic("outdoor", Vector3.zero, new Vector3(400, 60, 400));
            ContainerRegistry.Rebuild();
            var ship = MakeCarrier("ship", 42, new Vector3(20, 0, 0), new Vector3(20, 10, 40));
            var cabin = ContainerRegistry.RegisterRuntime(8, ContainerPlacement.Child(ship.Carried.ContainerId, new Vector3(0, 3, 0), new Vector3(6, 6, 6)));
            Assert.IsTrue(cabin.IsCarriedBy(ship), "a room fixed in the ship is in the ship's subtree");
            // The ship's own origin lies in the cabin: it resolves to the box around it, never the cabin.
            Assert.AreSame(ContainerRegistry.FindById("outdoor"), ContainerRegistry.Resolve(ship.transform.position + Vector3.up * 3, ship.Container, 0.35f, ship));
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("cannot be inside"));
            ship.SetContainer(cabin);
            Assert.AreEqual("outdoor", ship.Container.ContainerId);
        }

        [Test]
        public void ContentsOfAnInheritedChildLeaveWithTheBox()
        {
            MakeStatic("outdoor", Vector3.zero, new Vector3(400, 60, 400));
            ContainerRegistry.Rebuild();
            var ship = MakeCarrier("ship", 42, new Vector3(20, 0, 0), new Vector3(20, 10, 40));
            ContainerRegistry.RegisterRuntime(8, ContainerPlacement.Child(ship.Carried.ContainerId, new Vector3(0, 3, 10), new Vector3(6, 6, 6)));
            var crew = MakeEntity("crew", 100, new Vector3(20, 3, 10));
            Assert.AreEqual(ContainerRegistry.RuntimeContainerId(8), crew.Container.ContainerId);
            var contents = new List<NetworkIdentity>();
            ContainerRegistry.FindById("outdoor").CollectContents(contents, throughAuthoritativeCarriersOnly: false);
            CollectionAssert.Contains(contents, crew, "the crew in a room of the ship leaves with the ship");
        }

        // ------------------------------------------------------------------------------------ precision (21)

        [Test]
        public void PlacementFarFromTheOriginIsExact()
        {
            var definition = ScriptableObject.CreateInstance<WorldDefinition>();
            definition.CellSize = Vector3.one * 1000;
            try
            {
                NebulaWorld.LoadRuntime(definition);
                // 10,000 km out, with a fraction a float could not hold at that distance.
                const double far = 10_000_000.0;
                var center = new Double3(far + 12.3456, 7.25, -far - 0.875);
                NebulaWorld.Streamer.ShiftOrigin(new Vector3Int(10_000, 0, -10_000)); // the viewer is out there
                var box = ContainerRegistry.RegisterRuntime(99, ContainerPlacement.Root(center, new Vector3(10, 10, 10)));
                var p = box.transform.position;
                var back = ContainerRegistry.ToAbsolutePrecise(p, 0);
                Assert.That(back.X - center.X, Is.EqualTo(0).Within(0.01), "x within a centimetre");
                Assert.That(back.Y - center.Y, Is.EqualTo(0).Within(0.01));
                Assert.That(back.Z - center.Z, Is.EqualTo(0).Within(0.01), "z within a centimetre");
                Assert.That(p.x, Is.EqualTo(12.3456f).Within(0.001f));

                // A float row would have lost it: 10,000 km in a float has a resolution of a metre.
                float lossy = (float)(far + 12.3456);
                Assert.That(System.Math.Abs(lossy - (far + 12.3456)), Is.GreaterThan(0.01));
            }
            finally
            {
                NebulaWorld.Unload();
                Object.DestroyImmediate(definition);
            }
        }

        // ------------------------------------------------------------------------------------ wire and storage

        [Test]
        public void LeaseRowsRoundTripTheirPlacementThroughJson()
        {
            var cp = new LocalControlPlane();
            cp.Connect();
            var placement = ContainerPlacement.Child("rt_1", new Vector3(3, 4, 5), new Vector3(6, 7, 8), ContainerAuthority.Inherited).WithPhysicsFrame(FrameInterestMode.OwnRegions);
            cp.EnsureRuntimeContainer("rt_2", placement, "w1");
            cp.EnsureRuntimeContainer("rt_1", ContainerPlacement.Root(new Double3(10_000_000.125, 0, -3.5), new Vector3(1, 1, 1)), "w1");
            cp.EnsureContainer("ship#4", ContainerAuthority.Leased);
            var snapshot = ControlPlaneJson.Parse(cp.ToJson());
            var child = snapshot.Leases.Find(l => l.ContainerId == "rt_2");
            Assert.AreEqual("rt_1", child.ParentId);
            Assert.AreEqual(new Double3(3, 4, 5), child.Center);
            Assert.AreEqual(ContainerAuthority.Inherited, child.Authority);
            Assert.AreEqual(LeaseState.Inherited, child.State, "an inherited row is never assigned");
            Assert.AreEqual("", child.WorkerId);
            Assert.IsTrue(child.OwnPhysicsFrame);
            Assert.AreEqual(FrameInterestMode.OwnRegions, child.FrameInterest);
            var root = snapshot.Leases.Find(l => l.ContainerId == "rt_1");
            Assert.AreEqual(10_000_000.125, root.Center.X, "the centre comes back in double");
            Assert.AreEqual(ContainerAuthority.Leased, snapshot.Leases.Find(l => l.ContainerId == "ship#4").Authority);

            // The same through the write op a remote control plane sends.
            var op = new ControlPlaneJson.OpWriter();
            string body = "{\"ops\":[" + ControlPlaneJson.Placement(op.Op(ControlPlaneJson.EnsureRuntimeContainer).Arg("containerId", "rt_9").Arg("workerId", "w2"), placement).End() + "]}";
            var target = new LocalControlPlane();
            target.Connect();
            Assert.IsNull(ControlPlaneJson.ApplyBatch(body, target));
            var applied = target.FindLease("rt_9");
            Assert.AreEqual("rt_1", applied.ParentId);
            Assert.AreEqual(ContainerAuthority.Inherited, applied.Authority);
            Assert.IsTrue(applied.OwnPhysicsFrame);
        }

        [Test]
        public void OwnershipMessagesCarryPlacementsInATrailingSection()
        {
            var lease = new LeaseInfo { ContainerId = "rt_2", WorkerId = "w1", Epoch = 3, State = LeaseState.Active, HasBounds = true, ParentId = "rt_1", Center = new Double3(10_000_000.125, 2, 3), BoundsSize = new Vector3(4, 5, 6), OwnPhysicsFrame = true };
            var plain = new LeaseInfo { ContainerId = "rt_1", WorkerId = "w1", Epoch = 1, State = LeaseState.Active, HasBounds = true, Center = new Double3(1, 2, 3), BoundsSize = Vector3.one };
            var entries = new List<ContainerOwnershipEntry> { ContainerOwnershipEntry.Of(plain, ContainerRef.RuntimeIndex, 1), ContainerOwnershipEntry.Of(lease, ContainerRef.RuntimeIndex, 1) };
            Assert.IsFalse(entries[0].HasPlacement, "a plain root costs no extra bytes");
            var w = new NetworkWriter(256);
            ContainerOwnershipMsg.Write(w, entries);
            var bytes = w.ToArray();

            var r = new NetworkReader(bytes);
            r.ReadByte();
            var update = ContainerOwnershipMsg.Read(r);
            Assert.IsFalse(update.Upserts[0].HasPlacement);
            var e = update.Upserts[1];
            Assert.IsTrue(e.HasPlacement);
            Assert.AreEqual("rt_1", e.Placement.ParentId);
            Assert.AreEqual(10_000_000.125, e.Placement.Center.X);
            Assert.IsTrue(e.Placement.OwnPhysicsFrame);
            Assert.AreEqual(new Vector3(4, 5, 6), e.Placement.Size);

            // A reader that predates the section stops before it: everything it knew is still where it was.
            w.Reset();
            ContainerOwnershipMsg.Write(w, entries);
            var legacy = new NetworkReader(bytes);
            legacy.ReadByte();
            legacy.ReadByte();
            Assert.AreEqual(2, legacy.ReadUShort());
        }
    }
}
