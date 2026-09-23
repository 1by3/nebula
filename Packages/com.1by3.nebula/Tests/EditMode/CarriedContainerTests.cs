using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Containers carried by entities (<c>docs/container-tree.md</c> D1): a container on an entity's root is carried by it,
    /// registered by net id when it spawns and gone, with its riders set down, when it despawns; nesting, wire references,
    /// the carriers' bodies, and the obsolete DynamicContainer component and its migration.
    /// </summary>
    public class CarriedContainerTests
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

        /// <summary>A carrier entity (identity, root NetworkTransform and Container) with a box of <paramref name="size"/> centred above its origin, registered as if it had spawned.</summary>
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
            go.AddComponent<NetworkTransform>(); // with the Container on its root, the entity carries the box
            identity.Initialize();
            identity.NetId = netId;
            identity.OwnerWorkerIndex = 7;
            identity.SetContainer(ContainerRegistry.Find(position, box));
            identity.InvokeSpawn(); // NetworkIdentity registers the box it carries
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
            // The carrier's own position is excluded from its own box: it is the subject being placed.
            Assert.AreSame(outdoor, ContainerRegistry.Resolve(ship.transform.position, outdoor, 0.35f, ship));
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
            ship.InvokeDespawn(); // NetworkIdentity unregisters the box and evacuates it
            Assert.AreEqual(0, ContainerRegistry.Dynamic.Count);
            Assert.IsNull(ContainerRegistry.Resolve(ContainerRef.Dynamic(42)));
            Assert.AreEqual("outdoor", pawn.Container.ContainerId);
            Assert.AreNotSame(ship.transform, pawn.transform.parent);
            Assert.Less((pawn.transform.position - worldBefore).magnitude, 1e-4f, "evacuation keeps the world pose");
        }

        /// <summary>
        /// NEB-255: a carrier in a scoped chunk leaves with passengers aboard. They are put down in the carrier's own
        /// scope, in the scope's box that holds them or else the scope's nearest box, never in the public container
        /// around the same point nor in another scope's box that holds it.
        /// </summary>
        [Test]
        public void UnregisteringInAScopedChunkEvacuatesTheContentsWithinTheScope()
        {
            const ulong scope = 501, otherScope = 502;
            var chunk = ContainerRegistry.RegisterRuntime(9001, new Bounds(new Vector3(-20, 10, 0), new Vector3(40, 20, 40)),
                new InstanceContainerInfo { InstanceId = scope, ScopeKey = "scope/a" });
            // Another scope's copy of the space just past the chunk, where the ship's nose pokes out.
            var elsewhere = ContainerRegistry.RegisterRuntime(9002, new Bounds(new Vector3(10, 10, 0), new Vector3(20, 20, 40)),
                new InstanceContainerInfo { InstanceId = otherScope, ScopeKey = "scope/b" });
            NetworkIdentity ship = null, inside = null, outside = null;
            try
            {
                // A 20 m box from x = -15 to 5: the chunk ends at x = 0, the public "outdoor" box holds all of it.
                ship = MakeCarrier("ship", 42, new Vector3(-5, 0, 0), new Vector3(20, 6, 20));
                ship.SetContainer(chunk);
                Assume.That(ship.Carried.InstanceId, Is.EqualTo(scope), "the ship's box is in the chunk's scope");
                inside = MakeEntity("pawn", 43, new Vector3(-10, 1, 0));
                outside = MakeEntity("crate", 44, new Vector3(3, 1, 0));
                inside.SetContainer(ship.Carried);
                outside.SetContainer(ship.Carried);
                Assume.That(outside.InstanceId, Is.EqualTo(scope));

                ship.InvokeDespawn(); // NetworkIdentity unregisters the box and evacuates it

                Assert.AreSame(chunk, inside.Container, "the scope's box that holds the passenger, not the public box around the same point");
                Assert.AreSame(chunk, outside.Container, "no box of the scope holds this one: the scope's nearest box, not the other scope's box that does");
                Assert.AreEqual(scope, inside.InstanceId);
                Assert.AreEqual(scope, outside.InstanceId);
                CollectionAssert.IsEmpty(elsewhere.Entities);
            }
            finally
            {
                // A scoped box that still lists an entity refuses to be forgotten.
                foreach (var e in new[] { inside, outside, ship }) e?.SetContainer(null);
                ContainerRegistry.UnregisterRuntime(9001);
                ContainerRegistry.UnregisterRuntime(9002);
            }
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
            var entry = new EntityStateEntry { NetId = 9, Epoch = 1, Container = ContainerRef.Dynamic(42), LocalPosition = new Vector3(1, 2, 3), LocalRotation = Quaternion.identity, Velocity = Vector3.zero, Fields = TransformFields.Position };
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

        // ------------------------------------------------------------------------------------ the rule (D1)

        [Test]
        public void AContainerOnAnEntityRootRegistersOnSpawnAndUnregistersOnDespawn()
        {
            var go = new GameObject("lift");
            go.transform.position = new Vector3(-20, 0, 0);
            _objects.Add(go);
            var identity = go.AddComponent<NetworkIdentity>();
            var box = go.AddComponent<Container>();
            box.ContainerId = "lift";
            box.Size = new Vector3(10, 6, 20);
            box.Center = new Vector3(0, 3, 0);
            go.AddComponent<NetworkTransform>();
            ContainerRegistry.Rebuild();

            Assert.AreEqual(ContainerFrameMode.Entity, box.FrameMode, "on an entity's root: carried, whatever it is set to");
            Assert.IsFalse(box.IsDynamic, "not registered before the entity spawns");
            Assert.IsNull(ContainerRegistry.FindById("lift"), "never baked as a fixed container");
            Assert.AreEqual(ContainerAuthority.Inherited, box.ResolvedAuthority);

            identity.Initialize();
            identity.NetId = 77;
            identity.SetContainer(ContainerRegistry.Find(go.transform.position, box));
            identity.InvokeSpawn();
            Assert.AreSame(box, identity.Carried);
            Assert.IsTrue(box.IsDynamic);
            Assert.AreEqual("lift#77", box.ContainerId);
            Assert.AreSame(box, ContainerRegistry.Resolve(ContainerRef.Dynamic(77)));
            Assert.AreEqual("outdoor", identity.Container.ContainerId, "the carrier stands in the container around it, not in its own box");

            var rider = MakeEntity("rider", 78, new Vector3(-20, 1, 0));
            Assert.AreSame(box, rider.Container);
            identity.InvokeDespawn();
            Assert.IsFalse(box.IsDynamic);
            Assert.IsNull(ContainerRegistry.Resolve(ContainerRef.Dynamic(77)));
            Assert.AreEqual("outdoor", rider.Container.ContainerId, "the rider is set down in the container around the carrier");
            Assert.AreEqual(0, box.Entities.Count);
        }

        [Test]
        public void AContainerOnANonEntityIsBakedAndIgnoredBySpawn()
        {
            // An entity with a box on a child object: the box is not the entity's, it is baked like any other.
            var go = new GameObject("kiosk");
            go.transform.position = new Vector3(60, 0, -60);
            _objects.Add(go);
            var identity = go.AddComponent<NetworkIdentity>();
            var child = new GameObject("booth");
            child.transform.SetParent(go.transform, false);
            var box = child.AddComponent<Container>();
            box.ContainerId = "booth";
            box.Size = new Vector3(4, 3, 4);
            box.Center = new Vector3(0, 1.5f, 0);
            ContainerRegistry.Rebuild();

            Assert.AreEqual(ContainerFrameMode.Fixed, box.FrameMode);
            Assert.AreSame(box, ContainerRegistry.FindById("booth"), "baked");
            Assert.AreNotEqual(ushort.MaxValue, box.Index);
            StringAssert.Contains("child of entity", Container.PlacementProblem(box, out bool error));
            Assert.IsFalse(error, "a warning: it works, just not the way it looks");

            identity.Initialize();
            identity.NetId = 90;
            identity.SetContainer(ContainerRegistry.Find(go.transform.position));
            identity.InvokeSpawn();
            Assert.IsNull(identity.Carried, "the entity carries nothing");
            Assert.IsFalse(box.IsDynamic);
            Assert.AreSame(box, ContainerRegistry.FindById("booth"));
            identity.InvokeDespawn();
            Assert.AreSame(box, ContainerRegistry.FindById("booth"), "despawning the entity leaves the baked box alone");
        }

        [Test]
        public void AnEntityBoxWithoutARootNetworkTransformIsAnError()
        {
            var go = new GameObject("raft");
            _objects.Add(go);
            go.AddComponent<NetworkIdentity>();
            var box = go.AddComponent<Container>();
            StringAssert.Contains("needs a NetworkTransform", Container.PlacementProblem(box, out bool error));
            Assert.IsTrue(error);
            go.AddComponent<NetworkTransform>();
            Assert.IsNull(Container.PlacementProblem(box, out _));
        }

        [Test]
        public void TheCarriersBodyAndCollidersAreFoundWhileItsBoxIsRegistered()
        {
            var go = new GameObject("cart");
            go.transform.position = new Vector3(-20, 0, 0);
            _objects.Add(go);
            var identity = go.AddComponent<NetworkIdentity>();
            var box = go.AddComponent<Container>();
            box.Size = new Vector3(10, 6, 20);
            box.Center = new Vector3(0, 3, 0);
            go.AddComponent<NetworkTransform>();
            var body = go.AddComponent<Rigidbody>();
            body.isKinematic = true;
            var floor = new GameObject("floor");
            floor.transform.SetParent(go.transform, false);
            var floorCollider = floor.AddComponent<BoxCollider>();
            var loose = new GameObject("loose").AddComponent<BoxCollider>();
            _objects.Add(loose.gameObject);

            Assert.IsNull(Container.OfRigidbody(body), "nothing before the spawn");
            Assert.IsFalse(Container.IsCarrierGeometry(floorCollider));

            identity.Initialize();
            identity.NetId = 55;
            identity.SetContainer(ContainerRegistry.Find(go.transform.position, box));
            identity.InvokeSpawn();
            Assert.AreSame(box, Container.OfRigidbody(body));
            Assert.IsTrue(Container.IsCarrierGeometry(floorCollider), "the hull's colliders count as the carrier's");
            Assert.IsFalse(Container.IsCarrierGeometry(loose), "a collider with no body is the level");
            Assert.IsNull(Container.OfRigidbody(null));

            identity.InvokeDespawn();
            Assert.IsNull(Container.OfRigidbody(body));
            Assert.IsFalse(Container.IsCarrierGeometry(floorCollider));
        }

#pragma warning disable CS0618 // the obsolete component is what is under test
        [Test]
        public void TheObsoleteDynamicContainerIsInert()
        {
            var plain = MakeCarrier("plain", 60, new Vector3(-20, 0, 0), new Vector3(10, 6, 20));
            var go = new GameObject("legacy");
            go.transform.position = new Vector3(20, 0, 0);
            _objects.Add(go);
            var identity = go.AddComponent<NetworkIdentity>();
            var box = go.AddComponent<Container>();
            box.ContainerId = "legacy";
            box.Size = new Vector3(10, 6, 20);
            box.Center = new Vector3(0, 3, 0);
            go.AddComponent<NetworkTransform>();
            var legacy = go.AddComponent<DynamicContainer>();
            identity.Initialize();

            Assert.IsNotInstanceOf<NetworkBehaviour>(legacy);
            Assert.AreEqual(plain.Behaviours.Length, identity.Behaviours.Length, "it takes no behaviour slot");

            identity.NetId = 61;
            identity.SetContainer(ContainerRegistry.Find(go.transform.position, box));
            identity.InvokeSpawn();
            Assert.AreSame(box, identity.Carried, "the entity carries the box on its own");
            Assert.AreSame(box, legacy.Volume);
            Assert.IsTrue(legacy.IsRegistered);
            identity.InvokeDespawn();
            Assert.IsFalse(legacy.IsRegistered);
            Object.DestroyImmediate(legacy);
            Assert.IsFalse(box.IsDynamic, "removing it changes nothing");
        }

        [Test]
        public void TheMigrationStripsTheObsoleteComponentFromScenesAndPrefabs()
        {
            // An object in the open scene.
            var go = new GameObject("scene-ship");
            _objects.Add(go);
            go.AddComponent<NetworkIdentity>();
            go.AddComponent<Container>();
            go.AddComponent<NetworkTransform>();
            go.AddComponent<DynamicContainer>();
            var blocked = new List<string>();
            Assert.AreEqual(1, Nebula.Editor.NebulaMigrate.RemoveFrom(go, "test scene", blocked, inPrefabAsset: false));
            Assert.IsNull(go.GetComponent<DynamicContainer>());
            Assert.IsNotNull(go.GetComponent<Container>(), "the container itself stays");
            Assert.IsEmpty(blocked);

            // A prefab asset.
            const string folder = "Assets/NebulaMigrateTest";
            if (!UnityEditor.AssetDatabase.IsValidFolder(folder)) UnityEditor.AssetDatabase.CreateFolder("Assets", "NebulaMigrateTest");
            try
            {
                var source = new GameObject("prefab-ship");
                source.AddComponent<NetworkIdentity>();
                source.AddComponent<Container>();
                source.AddComponent<NetworkTransform>();
                source.AddComponent<DynamicContainer>();
                string path = folder + "/prefab-ship.prefab";
                UnityEditor.PrefabUtility.SaveAsPrefabAsset(source, path);
                Object.DestroyImmediate(source);
                var report = Nebula.Editor.NebulaMigrate.RemoveDynamicContainerEverywhere(new[] { folder });
                Assert.AreEqual(1, report.Prefabs);
                Assert.IsEmpty(report.Blocked);
                var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.IsNull(asset.GetComponent<DynamicContainer>());
                Assert.IsNotNull(asset.GetComponent<Container>());
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(folder); }
        }
#pragma warning restore CS0618
    }
}
