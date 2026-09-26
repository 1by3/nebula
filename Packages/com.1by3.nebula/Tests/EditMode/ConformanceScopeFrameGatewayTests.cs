using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// Conformance scenario 14 (<c>docs/conformance-suite.md</c>, design <c>docs/scope-frames.md</c> D4), the gateway
    /// half: a gateway that shares its process with a worker (the Editor's Multiplayer Play Mode loop, or any
    /// in-process host) shares that worker's containers, and the worker moves each scope's origin toward the chunks it
    /// leases. Every region key and every registry query of the gateway must still be in absolute coordinates of the
    /// scope, as the worker's are, or the gateway subscribes to regions the worker never files anything under and a
    /// client that joined far from the origin receives no new spawn or variable change (NEB-337).
    /// <para>
    /// Tier B: one real <see cref="NebulaWorker"/> on the <see cref="ConformanceMesh"/> and one real
    /// <see cref="NebulaGateway"/> in the same process, over the same <see cref="ContainerRegistry"/> and the same
    /// <see cref="ScopeFrames"/>. The gateway is fed the worker's own spawn messages and runs its own interest
    /// evaluation; the subscription it arrives at is handed to the worker as the gateway's link would carry it, and the
    /// worker must answer with the spawn. The gateway's socket, its worker links and its control plane are not
    /// involved: its lease rows are written in directly, its transport only records.
    /// </para>
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceScopeFrameGatewayTests
    {
        private const string Planet = "world/planet";
        private const ulong ClientId = 42;
        private static readonly Vector3 Cell = new Vector3(256f, 256f, 256f);
        /// <summary>The chunk of the report: absolute (1900, 1100) on a 256 m grid.</summary>
        private static readonly Vector3Int Far = new Vector3Int(7, 0, 4);
        /// <summary>Where the shifted frame puts another worker's chunk: on top of the absolute coordinates of <see cref="Far"/>.</summary>
        private static readonly Vector3Int Decoy = new Vector3Int(14, 0, 8);
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type GatewayType = typeof(NebulaGateway);

        private ConformanceMesh _mesh;
        private RuntimeGrid _grid;
        private WorldDefinition _definition;
        private ushort _prefabId;
        private GameObject _gatewayHost;
        private NebulaGateway _gateway;

        [SetUp]
        public void SetUp()
        {
            _definition = ScriptableObject.CreateInstance<WorldDefinition>();
            _definition.CellSize = Cell;
            NebulaWorld.LoadRuntime(_definition);
            _mesh = new ConformanceMesh(1);
            _grid = new RuntimeGrid(Cell, planar: true, scopeKey: Planet);
            NebulaChunks.Activate(_grid, NebulaRoles.Worker, true, null);
            var prefab = new GameObject("rock-prefab");
            prefab.AddComponent<NetworkIdentity>();
            _prefabId = _mesh.RegisterPrefab(prefab);
            StateHistory.WindowTicks = 8;
        }

        [TearDown]
        public void TearDown()
        {
            if (_gatewayHost != null) Object.DestroyImmediate(_gatewayHost);
            _mesh.Dispose();
            NebulaChunks.ResetForNewSession();
            foreach (var c in new List<Container>(ContainerRegistry.Runtime)) if (c != null) Object.DestroyImmediate(c.gameObject);
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.RuntimeBoundsInFrame = null;
            ContainerRegistry.Rebuild();
            NebulaWorld.Unload();
            Object.DestroyImmediate(_definition);
        }

        /// <summary>One chunk of the planet, registered from its lease row and leased to <paramref name="owner"/>.</summary>
        private Container Chunk(Vector3Int coord, string owner, ushort ownerIndex)
        {
            var container = ContainerRegistry.RegisterRuntime(_grid.IdOf(coord), _grid.BoundsOf(coord), new InstanceContainerInfo
            {
                InstanceId = _grid.InstanceId,
                ScopeKey = _grid.ScopeKey,
                PartId = ChunkKeys.PartId(coord),
            });
            ContainerRegistry.ApplyLease(container.ContainerId, owner, ownerIndex, 1);
            return container;
        }

        // ------------------------------------------------------------------------------------ gateway plumbing

        private object Field(string name) => GatewayType.GetField(name, Flags).GetValue(_gateway);
        private object Call(string name, params object[] args) => GatewayType.GetMethod(name, Flags).Invoke(_gateway, args);

        /// <summary>A gateway in this process with its interest set up and a transport that only records.</summary>
        private void StartGateway(params Container[] leased)
        {
            _gatewayHost = new GameObject("gateway");
            _gateway = _gatewayHost.AddComponent<NebulaGateway>();
            GatewayType.GetField("<Config>k__BackingField", Flags).SetValue(_gateway, _mesh.Config);
            GatewayType.GetField("_transport", Flags).SetValue(_gateway, new ConformanceMesh.RecordingTransport());
            Call("InitializeInterest");
            // The rows the control plane would have handed it, keyed as OnControlPlaneChanged keys them.
            var rows = (IDictionary)Field("_ownershipById");
            foreach (var c in leased)
                rows[c.ContainerId] = new ContainerOwnershipEntry
                {
                    Instance = c.Instance, ContainerIndex = ContainerRef.RuntimeIndex, ContainerId = c.ContainerId,
                    WorkerId = c.OwnerWorkerId, WorkerIndex = c.OwnerWorkerIndex, Epoch = 1, State = LeaseState.Active,
                };
        }

        /// <summary>A worker link as the gateway holds one, for the handlers that check who sent a message.</summary>
        private object WorkerConn(ConformanceMesh.Worker worker)
        {
            var type = GatewayType.GetNestedType("WorkerConn", BindingFlags.NonPublic);
            var conn = Activator.CreateInstance(type, true);
            type.GetField("WorkerId").SetValue(conn, worker.Id);
            type.GetField("Index").SetValue(conn, worker.Index);
            type.GetField("Ready").SetValue(conn, true);
            return conn;
        }

        /// <summary>A joined client of this gateway, before its pawn exists.</summary>
        private object Client()
        {
            var type = GatewayType.GetNestedType("ClientConn", BindingFlags.NonPublic);
            var client = Activator.CreateInstance(type, true);
            type.GetField("ClientId").SetValue(client, ClientId);
            type.GetField("PeerId").SetValue(client, 7);
            type.GetField("Welcomed").SetValue(client, true);
            type.GetField("ScopeKey").SetValue(client, Planet);
            type.GetField("LastScope").SetValue(client, _grid.InstanceId);
            ((IDictionary)Field("_clientsById")).Add(ClientId, client);
            return client;
        }

        private static T Of<T>(object client, string field) => (T)client.GetType().GetField(field).GetValue(client);

        /// <summary>The worker's spawn of <paramref name="entity"/>, as it sends it to a gateway, delivered into the gateway.</summary>
        private void Announce(ConformanceMesh.Worker worker, NetworkIdentity entity, ulong ownerClientId = 0)
        {
            var msg = EntitySpawnMsg.From(entity, new NetworkWriter(256), forGateway: true);
            msg.OwnerClientId = ownerClientId;
            Call("OnEntitySpawn", WorkerConn(worker), msg);
        }

        private ulong WorkerRegionOf(ConformanceMesh.Worker worker, NetworkIdentity entity)
        {
            ulong region = 0;
            worker.Act(() => region = (ulong)typeof(NebulaWorker).GetMethod("RegionOf", Flags, null, new[] { typeof(NetworkIdentity) }, null)
                .Invoke(worker.Instance, new object[] { entity }));
            return region;
        }

        private ulong GatewayRegionOf(ulong netId)
        {
            var record = ((IDictionary)Field("_entities"))[netId];
            return (ulong)GatewayType.GetMethod("RegionOf", Flags).Invoke(_gateway, new[] { record });
        }

        // ------------------------------------------------------------------------------------ the scenario

        [Test]
        public void AClientJoiningFarFromTheOriginGetsNewSpawnsWhileItsWorkerFollowsItsChunks()
        {
            var w1 = _mesh[0];
            var here = Chunk(Far, w1.Id, w1.Index);
            var decoy = Chunk(Decoy, "w-decoy", 9);
            // The player's saved position, and a rock spawned 2 m away: absolute (1898, 1100) and (1900, 1100).
            var pawn = w1.SpawnServerDriven(_prefabId, here, new Vector3(1898f, 0f, 1100f), Quaternion.identity);
            var rock = w1.SpawnServerDriven(_prefabId, here, new Vector3(1900f, 0f, 1100f), Quaternion.identity);

            // What NebulaChunkedWorld.FollowOwnedCells does on the worker: the planet's origin moves to the chunk it
            // leases, and every container of the planet, shared with the gateway in this process, moves with it.
            _grid.KeepOriginNear(Far, 1);
            Assert.AreEqual(Far, _grid.Frame.Cell, "the worker's frame followed its chunk");
            Assert.Less(here.transform.position.magnitude, Cell.x * 2f, "and the chunk the gateway also reads has moved to the origin");

            StartGateway(here, decoy);
            var client = Client();
            Announce(w1, pawn, ClientId);
            Announce(w1, rock);

            // 1. The gateway files each entity where the worker does: absolute coordinates in the scope.
            ulong rockRegion = WorkerRegionOf(w1, rock);
            Assert.AreEqual(rockRegion, GatewayRegionOf(rock.NetId), "the gateway's region of the rock is the worker's");
            Assert.AreEqual(WorkerRegionOf(w1, pawn), GatewayRegionOf(pawn.NetId), "and so is the pawn's");

            // 2. Its interest evaluation subscribes the region the worker holds the rock in, from the owner of that
            // region: the registry is asked in the frame it holds the planet's boxes in, not at the absolute numbers,
            // where the shifted frame has put the decoy chunk.
            Call("EvaluateClient", client, 0.0);
            var regions = Of<HashSet<ulong>>(client, "Regions");
            CollectionAssert.Contains(regions, rockRegion, "the client's subscription covers the rock's region");
            var owners = (List<string>)Call("WorkersForRegion", rockRegion);
            CollectionAssert.AreEqual(new[] { w1.Id }, owners, "and it is asked of the worker that leases the chunk");
            CollectionAssert.Contains(Of<HashSet<string>>(client, "KnownContainers"), here.ContainerId, "the client was sent its chunk's row");
            CollectionAssert.DoesNotContain(Of<HashSet<string>>(client, "KnownContainers"), decoy.ContainerId, "and not a chunk 2 km away");

            // 3. That subscription, carried to the worker as the gateway's link would carry it, gets the rock sent.
            var gateway = _mesh.AddGateway("gw1");
            _mesh.LinkGateway(gateway);
            w1.Tick(1);
            _mesh.Pump();
            _mesh.Delivered.Clear();
            var set = regions.ToList();
            _mesh.FromGateway(gateway, w1, w => new InterestSubscribeMsg
            {
                Seq = 1, Grid = (InterestGrid)Field("_interestGrid"),
                Flags = InterestSubscribeFlags.Full | InterestSubscribeFlags.Commit,
                Add = set, Remove = new List<ulong>(), FociRegions = new List<ulong>(), Entities = new List<ulong>(),
                SetCount = (uint)set.Count, SetHash = RegionSubscription.Hash(set),
            }.Write(w));
            w1.Tick(2);
            _mesh.Pump();
            var spawned = _mesh.DeliveredOf(MsgId.EntitySpawn, gateway.Id).Select(m => m.Read(r => EntitySpawnMsg.Read(r)).NetId).ToList();
            CollectionAssert.Contains(spawned, rock.NetId, "the worker sends the gateway the rock its client is standing next to");
        }

        /// <summary>
        /// NEB-336: an entity whose <see cref="NetworkIdentity.RelevanceRadius"/> is larger than the interest radius is a
        /// wide entity, matched against each gateway's foci rather than bucketed by region. In a scoped grid it must
        /// still reach the client standing next to it and the client inside its radius but outside the interest radius,
        /// on the gateway (the client's set) and on the worker (the gateways it is published to).
        /// </summary>
        [Test]
        public void AWideEntityInAScopedGridReachesClientsWithinItsRelevanceRadius([Values(2f, 150f)] float distance)
        {
            var w1 = _mesh[0];
            var here = Chunk(Far, w1.Id, w1.Index);
            var wide = new GameObject("beacon-prefab");
            var wideId = wide.AddComponent<NetworkIdentity>();
            float interest = _mesh.Config.InterestRadius;
            wideId.RelevanceRadius = interest + 80f;
            Assume.That(distance, Is.LessThan(wideId.RelevanceRadius));
            ushort widePrefab = _mesh.RegisterPrefab(wide);

            var pawn = w1.SpawnServerDriven(_prefabId, here, new Vector3(1900f - distance, 0f, 1100f), Quaternion.identity);
            var beacon = w1.SpawnServerDriven(widePrefab, here, new Vector3(1900f, 0f, 1100f), Quaternion.identity);
            Assert.AreEqual(wideId.RelevanceRadius, beacon.RelevanceRadius, "the spawned beacon keeps its prefab's radius");
            _grid.KeepOriginNear(Far, 1);

            StartGateway(here);
            var client = Client();
            Announce(w1, pawn, ClientId);
            Announce(w1, beacon);

            // 1. The gateway: the client's set holds the beacon.
            Call("EvaluateClient", client, 0.0);
            CollectionAssert.Contains(Of<HashSet<ulong>>(client, "Visible"), beacon.NetId,
                $"a client {distance} m from a wide entity in a scoped grid is sent it");

            // 2. The worker: the gateway's foci, carried as its link would carry them, get the beacon published to it.
            var gateway = _mesh.AddGateway("gw1");
            _mesh.LinkGateway(gateway);
            w1.Tick(1);
            _mesh.Pump();
            _mesh.Delivered.Clear();
            var regions = Of<HashSet<ulong>>(client, "Regions").ToList();
            var foci = new List<ulong>();
            var interestOf = client.GetType().GetField("Interest").GetValue(client);
            var focusList = (IReadOnlyList<InterestFocus>)interestOf.GetType().GetProperty("Foci").GetValue(interestOf);
            var grid = (InterestGrid)Field("_interestGrid");
            foreach (var f in focusList) foci.Add(RegionKeys.Salt(grid.RegionOf(f.X, f.Y, f.Z), _grid.InstanceId, f.Space));
            Assert.IsNotEmpty(foci, "the client has a focus");
            _mesh.FromGateway(gateway, w1, w => new InterestSubscribeMsg
            {
                Seq = 1, Grid = grid,
                Flags = InterestSubscribeFlags.Full | InterestSubscribeFlags.Commit,
                Add = regions, Remove = new List<ulong>(), FociRegions = foci, Entities = new List<ulong>(),
                SetCount = (uint)regions.Count, SetHash = RegionSubscription.Hash(regions),
            }.Write(w));
            w1.Tick(2);
            _mesh.Pump();
            var spawned = _mesh.DeliveredOf(MsgId.EntitySpawn, gateway.Id).Select(m => m.Read(r => EntitySpawnMsg.Read(r)).NetId).ToList();
            CollectionAssert.Contains(spawned, beacon.NetId, "the worker publishes the wide entity to the gateway whose client is within its radius");
            Object.DestroyImmediate(wide);
        }

        [Test]
        public void TheGatewaysWorldPositionsAreAbsoluteWhateverTheSharedFrameDid()
        {
            var w1 = _mesh[0];
            var here = Chunk(Far, w1.Id, w1.Index);
            var rock = w1.SpawnServerDriven(_prefabId, here, new Vector3(1900f, 0f, 1100f), Quaternion.identity);
            StartGateway(here);
            Announce(w1, rock);
            var local = rock.LocalPosition;

            var before = (Vector3)Call("WorldPosition", rock.ContainerRef, local);
            _grid.KeepOriginNear(Far, 1);
            var after = (Vector3)Call("WorldPosition", rock.ContainerRef, local);

            Assert.AreEqual(1900f, before.x, 1e-3f);
            Assert.AreEqual(1100f, before.z, 1e-3f);
            Assert.AreEqual(before.x, after.x, 1e-3f, "a shift of the frame the gateway shares does not move an absolute position");
            Assert.AreEqual(before.z, after.z, 1e-3f);
            var back = (Vector3)Call("ContainerPosition", rock.ContainerRef, after);
            Assert.AreEqual(local.x, back.x, 1e-3f, "and ContainerPosition is still its inverse");
            Assert.AreEqual(local.z, back.z, 1e-3f);
        }
    }
}
