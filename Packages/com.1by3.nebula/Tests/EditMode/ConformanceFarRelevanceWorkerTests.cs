using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 44 on a real <see cref="NebulaWorker"/> (NEB-388, NEB-391; <c>docs/interest-management.md</c>
    /// §16, §17): what a worker sends a gateway linked to it (<see cref="ConformanceMesh"/>) whose only client focus is at
    /// the origin.
    /// <list type="bullet">
    /// <item>A carrier whose relevance radius is over the mesh radius reaches the gateway at that radius, crew and all,
    /// although the gateway subscribes no region near it, and is forgotten contents first once it is beyond it (NEB-391:
    /// it used to be downgraded to a region entity, so the gateway heard nothing past ~200 m).</item>
    /// <item>A far entity 50 km out is sent to the gateway at its far rate as an absolute pose in double; one beyond its
    /// far radius is not sent at all; a despawn goes to the gateway that held it far.</item>
    /// <item>A far entity standing in a turning planet's frame is sent where the planet carries it, in the scope's own
    /// space, within a centimetre.</item>
    /// </list>
    /// The service suite's <c>ConformanceFarRelevanceTests</c> runs the gateway and client halves end to end.
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceFarRelevanceWorkerTests
    {
        private ConformanceMesh _mesh;
        private ConformanceMesh.Gateway _gateway;
        private Container _space;
        private uint _tick = 1000;
        private uint _seq;

        private ConformanceMesh.Worker W1 => _mesh[0];

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
            _mesh = new ConformanceMesh(1);
            _mesh.Config.GhostBandMargin = -1f;
            _space = _mesh.AddStaticContainer("system", Vector3.zero, new Vector3(400000f, 400000f, 400000f));
            _space.Center = Vector3.zero;
            ContainerRegistry.Rebuild();
            _mesh.SetOwner(_space, W1);
            _gateway = _mesh.AddGateway("gw1");
            _mesh.LinkGateway(_gateway);
        }

        [TearDown]
        public void TearDown()
        {
            _mesh.Dispose();
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
        }

        // ------------------------------------------------------------------------------------ plumbing

        private ushort Prefab(string name, float relevanceRadius = 0f, float farRadius = 0f, bool carrier = false, bool frame = false, FrameInterestMode interest = FrameInterestMode.WithCarrier, Vector3? box = null)
        {
            var go = new GameObject(name);
            var id = go.AddComponent<NetworkIdentity>();
            id.RelevanceRadius = relevanceRadius;
            id.FarRelevanceRadius = farRadius;
            if (carrier)
            {
                var c = go.AddComponent<Container>();
                c.ContainerId = name;
                c.Size = box ?? new Vector3(10f, 6f, 20f);
                c.Center = new Vector3(0f, c.Size.y * 0.5f, 0f);
                c.OwnPhysicsFrame = frame;
                c.FrameInterest = interest;
            }
            go.AddComponent<NetworkTransform>();
            return _mesh.RegisterPrefab(go);
        }

        /// <summary>
        /// The gateway's whole subscription: the regions within 200 m of the origin and one focus there, in the public
        /// world's own space, as a gateway with one client standing at the origin sends it.
        /// </summary>
        private void Subscribe()
        {
            var grid = InterestGrid.Resolve(InterestSettings.Default);
            var set = new HashSet<ulong>();
            for (float x = -200f; x <= 200f; x += 16f)
                for (float z = -200f; z <= 200f; z += 16f)
                    set.Add(grid.RegionOf(x, 0, z));
            var regions = new List<ulong>(set);
            uint seq = ++_seq;
            _mesh.FromGateway(_gateway, W1, writer => new InterestSubscribeMsg
            {
                Seq = seq, Grid = grid,
                Flags = InterestSubscribeFlags.Full | InterestSubscribeFlags.Commit,
                Add = regions, Remove = new List<ulong>(), FociRegions = new List<ulong> { grid.RegionOf(0, 0, 0) }, Entities = new List<ulong>(),
                SetCount = (uint)regions.Count, SetHash = RegionSubscription.Hash(regions),
            }.Write(writer));
        }

        private void Run(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                _tick++;
                W1.Tick(_tick);
                _mesh.Pump();
            }
        }

        private List<string> ToGateway()
        {
            var said = new List<string>();
            foreach (var m in _mesh.Delivered)
            {
                if (m.To != _gateway.Id) continue;
                switch (m.Id)
                {
                    case MsgId.EntitySpawn: said.Add("spawn " + m.Read(r => EntitySpawnMsg.Read(r)).NetId); break;
                    case MsgId.EntityForget: said.Add("forget " + m.Read(r => EntityForgetMsg.Read(r)).NetId); break;
                    case MsgId.EntityDespawn: said.Add("despawn " + m.Read(r => EntityDespawnMsg.Read(r)).NetId); break;
                    case MsgId.WorldState:
                    {
                        var reader = new NetworkReader(m.Bytes);
                        reader.ReadByte();
                        WorldStateMsg.ReadHeader(reader, out _, out _, out ushort count);
                        for (int i = 0; i < count; i++) said.Add("state " + EntityStateEntry.Read(reader).NetId);
                        break;
                    }
                }
            }
            return said;
        }

        private List<FarEntityEntry> FarToGateway()
        {
            var entries = new List<FarEntityEntry>();
            var scratch = new List<FarEntityEntry>();
            foreach (var m in _mesh.Delivered)
            {
                if (m.To != _gateway.Id || m.Id != MsgId.FarEntities) continue;
                var reader = new NetworkReader(m.Bytes);
                reader.ReadByte();
                FarEntitiesMsg.Read(reader, scratch);
                entries.AddRange(scratch);
            }
            return entries;
        }

        private NetworkIdentity SpawnIn(ushort prefab, Container container, Vector3 local)
        {
            NetworkIdentity e = null;
            W1.Act(() =>
            {
                e = NetworkPrefabs.Instantiate(prefab, local, Quaternion.identity, container.ContentRoot);
                W1.Instance.SpawnServerDriven(e, container);
            });
            if (container.InnerSpace != null && container.InnerSpace.Frame != null) e.transform.position = container.InnerSpace.Frame.LocalToSimulation(local);
            else e.transform.position = container.transform.TransformPoint(local);
            return e;
        }

        // ------------------------------------------------------------------------------------ NEB-391

        [Test]
        public void ACarrierReachesTheGatewayAtItsOwnRadiusWithItsCrewAndIsForgottenBeyondIt()
        {
            var ship = W1.SpawnServerDriven(Prefab("ship", relevanceRadius: 600f, carrier: true), _space, new Vector3(500f, 0f, 0f), Quaternion.identity);
            var crew = SpawnIn(Prefab("crew"), ship.Carried, new Vector3(0f, 1f, 2f));
            Assert.AreEqual(ship.Carried, crew.Container, "the crew member rides in the ship");
            Run(2);
            _mesh.Delivered.Clear();

            Subscribe();
            for (int i = 0; i < 5; i++) { ship.transform.position += new Vector3(0f, 0f, 1f); Run(1); }
            var heard = ToGateway();
            TestContext.WriteLine(string.Join(", ", heard));
            int shipSpawn = heard.IndexOf("spawn " + ship.NetId), crewSpawn = heard.IndexOf("spawn " + crew.NetId);
            Assert.That(shipSpawn, Is.GreaterThanOrEqualTo(0), "a ship 500 m out with a 600 m radius reaches the gateway, whose regions stop at 200 m");
            Assert.That(crewSpawn, Is.GreaterThan(shipSpawn), "and its crew with it, carrier first");
            Assert.That(heard, Does.Contain("state " + ship.NetId), "and it keeps getting the ship's state, not a frozen copy");
            Assert.IsTrue(W1.Instance.TryGetCarrierReach(ship.NetId, out ulong reach) && reach != 0);

            // Out past the radius and the exit margin: forgotten, contents first.
            _mesh.Delivered.Clear();
            ship.transform.position = new Vector3(700f, 0f, 0f);
            Subscribe();
            Run(3);
            heard = ToGateway();
            TestContext.WriteLine(string.Join(", ", heard));
            int crewForget = heard.IndexOf("forget " + crew.NetId), shipForget = heard.IndexOf("forget " + ship.NetId);
            Assert.That(crewForget, Is.GreaterThanOrEqualTo(0), "the crew member is forgotten");
            Assert.That(shipForget, Is.GreaterThan(crewForget), "before the ship");
            _mesh.Delivered.Clear();
            Run(5);
            Assert.That(ToGateway(), Does.Not.Contain("state " + ship.NetId), "and nothing more is sent about it");
        }

        [Test]
        public void TwoShips500MetresApartAtDefaultSettingsAreSentToEachOthersGateway()
        {
            // Default settings: InterestRadius 120, InterestMaxRadius 1024. The ship prefab asks for 600 m.
            var shipPrefab = Prefab("ship", relevanceRadius: 600f, carrier: true);
            var near = W1.SpawnServerDriven(shipPrefab, _space, new Vector3(0f, 0f, 0f), Quaternion.identity);
            var other = W1.SpawnServerDriven(shipPrefab, _space, new Vector3(0f, 0f, 500f), Quaternion.identity);
            Subscribe(); // the gateway of the pilot of `near`, its focus at near's position
            for (int i = 0; i < 4; i++) { other.transform.position += Vector3.right * 0.5f; Run(1); }
            var heard = ToGateway();
            Assert.That(heard, Does.Contain("spawn " + other.NetId));
            Assert.That(heard, Does.Contain("state " + other.NetId), "the other ship is replicated, not frozen");
            Assert.That(near, Is.Not.Null);
        }

        // ------------------------------------------------------------------------------------ NEB-388

        [Test]
        public void AFarShipIsSentAtItsFarRateAsAnAbsolutePoseAndOneBeyondItsRadiusIsNot()
        {
            var farShip = Prefab("far-ship", farRadius: 100000f, carrier: true);
            var ship = W1.SpawnServerDriven(farShip, _space, new Vector3(50000f, 0f, 0f), Quaternion.Euler(0f, 30f, 0f));
            var beyond = W1.SpawnServerDriven(farShip, _space, new Vector3(0f, 0f, 150000f), Quaternion.identity);
            Subscribe();
            _mesh.Delivered.Clear();
            Run(NetworkTime.TickRate * 3);
            var far = FarToGateway();
            int forShip = 0, forBeyond = 0;
            foreach (var e in far)
            {
                if (e.NetId == ship.NetId && e.Kind == FarEntryKind.State) forShip++;
                if (e.NetId == beyond.NetId) forBeyond++;
            }
            TestContext.WriteLine($"far entries in 3 s: ship {forShip}, beyond {forBeyond}; worker sent {W1.Instance.FarEntriesSent}");
            Assert.That(forShip, Is.InRange(3, 4), "one a second (FarUpdateRate 1) for 3 s");
            Assert.AreEqual(0, forBeyond, "a ship 150 km out with a 100 km far radius is not sent");
            var entry = far.Find(e => e.NetId == ship.NetId);
            Assert.AreEqual(50000.0, entry.X, 1e-3);
            Assert.AreEqual(0.0, entry.Z, 1e-3);
            Assert.AreEqual(100000f, entry.Radius);
            Assert.AreEqual(1f, entry.UpdateRate);
            Assert.That(Quaternion.Angle(entry.Rotation, Quaternion.Euler(0f, 30f, 0f)), Is.LessThan(0.5f));
            Assert.That(ToGateway(), Does.Not.Contain("spawn " + ship.NetId), "a far entity is not spawned to the gateway");
            Assert.IsTrue(W1.Instance.HasFarEntities, "and the heartbeat says this worker holds far entities");

            _mesh.Delivered.Clear();
            W1.Act(() => W1.Instance.Despawn(ship));
            _mesh.Pump();
            Assert.That(ToGateway(), Does.Contain("despawn " + ship.NetId), "a despawn goes to the gateway that held it far");
        }

        [TestCase(0f, TestName = "AFarShipOnAPlanetIsSentWhereThePlanetCarriesIt(planet still)")]
        [TestCase(90f, TestName = "AFarShipOnAPlanetIsSentWhereThePlanetCarriesIt(planet turned 90 degrees)")]
        public void AFarShipOnAPlanetIsSentWhereThePlanetCarriesIt(float yaw)
        {
            var planetAt = new Vector3(120000f, 0f, 0f);
            var planet = W1.SpawnServerDriven(Prefab("planet", carrier: true, frame: true, interest: FrameInterestMode.OwnRegions, box: new Vector3(8000f, 1000f, 8000f)), _space, planetAt, Quaternion.identity);
            var chunk = ContainerRegistry.RegisterRuntime(9001UL,
                ContainerPlacement.Child(planet.Carried.ContainerId, new Vector3(0f, 100f, 0f), new Vector3(8000f, 1000f, 8000f), ContainerAuthority.Leased));
            Assert.IsNotNull(chunk);
            ContainerRegistry.ApplyLease(chunk.ContainerId, W1.Id, W1.Index, 1);
            planet.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            Run(2);
            var local = new Vector3(1000f, 2f, 500f);
            var ship = SpawnIn(Prefab("far-ship", farRadius: 200000f), chunk, local);
            Assert.AreEqual(planet.Carried, ship.Space, "the ship stands in the planet's frame");
            Subscribe();
            _mesh.Delivered.Clear();
            Run(NetworkTime.TickRate + 1);
            var far = FarToGateway().FindAll(e => e.NetId == ship.NetId && e.Kind == FarEntryKind.State);
            Assert.That(far.Count, Is.GreaterThanOrEqualTo(1), "sent although it stands in a frame with regions of its own");
            var truth = planet.transform.TransformPoint(local);
            var entry = far[far.Count - 1];
            double error = Math.Sqrt(entry.SqrDistanceTo(truth.x, truth.y, truth.z));
            TestContext.WriteLine($"planet yaw {yaw}: sent ({entry.X:0.000}, {entry.Y:0.000}, {entry.Z:0.000}), truth {truth}, error {error * 1000:0.0} mm");
            Assert.That(error, Is.LessThan(0.01), "within a centimetre of where the planet carries it, in the scope's own space");
            Assert.That(Quaternion.Angle(entry.Rotation, planet.transform.rotation), Is.LessThan(0.5f), "and turned with the planet");
        }
    }
}
