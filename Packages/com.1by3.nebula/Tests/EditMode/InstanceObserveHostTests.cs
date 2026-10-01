using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// An instance can keep observing its host scope (<see cref="ScopeDefinition.ObserveHost"/>,
    /// <c>docs/scope-activation.md</c> §12). The option travels from a template or a definition to the lease row; the
    /// gateway keeps the host's container rows around where the pawn left it in the occupant's view, and lets them go
    /// as before once the pawn has left both; and a client hides a scoped grid's chunks while its pawn is elsewhere.
    /// The gateway half is tier B: a real <see cref="NebulaGateway"/> in this process, with its lease rows written in
    /// directly and its pawn record placed by hand, as <see cref="ConformanceScopeFrameGatewayTests"/> does.
    /// </summary>
    public sealed class InstanceObserveHostTests
    {
        private const string HostKey = "world/host";
        private const ulong ClientId = 42;
        private const ulong PawnNetId = 9001;
        private static readonly Vector3 Cell = new Vector3(256f, 256f, 256f);
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type GatewayType = typeof(NebulaGateway);

        private ConformanceMesh _mesh;
        private WorldDefinition _definition;
        private RuntimeGrid _host;
        private GameObject _gatewayHost;
        private NebulaGateway _gateway;
        private object _client;
        private object _pawn;
        private bool _wasClient;

        [SetUp]
        public void SetUp()
        {
            _definition = ScriptableObject.CreateInstance<WorldDefinition>();
            _definition.CellSize = Cell;
            NebulaWorld.LoadRuntime(_definition);
            _mesh = new ConformanceMesh(1);
            _host = new RuntimeGrid(Cell, planar: false, scopeKey: HostKey);
            _wasClient = NebulaRuntime.IsClient;
        }

        [TearDown]
        public void TearDown()
        {
            NebulaRuntime.IsClient = _wasClient;
            if (_gatewayHost != null) Object.DestroyImmediate(_gatewayHost);
            _mesh.Dispose();
            NebulaChunks.ResetForNewSession();
            InstanceScenes.Reset();
            foreach (var c in new List<Container>(ContainerRegistry.Runtime)) if (c != null) Object.DestroyImmediate(c.gameObject);
            ContainerRegistry.PruneRuntime(new HashSet<ulong>());
            ContainerRegistry.RuntimeBoundsInFrame = null;
            ContainerRegistry.Rebuild();
            NebulaWorld.Unload();
            Object.DestroyImmediate(_definition);
        }

        // ------------------------------------------------------------------------------------ the world

        /// <summary>A chunk of the host grid, registered from its lease row and leased to the named worker.</summary>
        private Container HostChunk(Vector3Int coord, string owner = "w-1", ushort ownerIndex = 1)
        {
            var c = ContainerRegistry.RegisterRuntime(_host.IdOf(coord), _host.BoundsOf(coord), new InstanceContainerInfo
            {
                InstanceId = _host.InstanceId, ScopeKey = _host.ScopeKey, PartId = ChunkKeys.PartId(coord),
            });
            ContainerRegistry.ApplyLease(c.ContainerId, owner, ownerIndex, 1);
            return c;
        }

        /// <summary>A cell of the public world: a runtime container with no scope.</summary>
        private static Container PublicCell(Vector3Int coord)
        {
            var bounds = new Bounds(Vector3.Scale(coord, Cell) + Cell * 0.5f, Cell);
            var c = ContainerRegistry.RegisterRuntime(RuntimeGrid.PackId(coord), bounds);
            ContainerRegistry.ApplyLease(c.ContainerId, "w-1", 1, 1);
            return c;
        }

        /// <summary>A part of an instance, as a boundary at <paramref name="center"/> would prepare it.</summary>
        private static Container Instance(string key, Vector3 center, bool observeHost, string part = "interior")
        {
            string scopeKey = "vault/" + key;
            var c = ContainerRegistry.RegisterRuntime(NebulaWorker.InstanceKey(scopeKey + "/" + part), new Bounds(center, new Vector3(40, 20, 40)), new InstanceContainerInfo
            {
                InstanceId = NebulaWorker.InstanceKey(scopeKey), ScopeKey = scopeKey, PartId = part, ObserveHost = observeHost,
            });
            ContainerRegistry.ApplyLease(c.ContainerId, "w-1", 1, 1);
            return c;
        }

        // ------------------------------------------------------------------------------------ gateway plumbing

        private object Field(string name) => GatewayType.GetField(name, Flags).GetValue(_gateway);
        private object Call(string name, params object[] args) => GatewayType.GetMethod(name, Flags).Invoke(_gateway, args);
        private static T Of<T>(object target, string field) => (T)target.GetType().GetField(field).GetValue(target);
        private static void Set(object target, string field, object value) => target.GetType().GetField(field).SetValue(target, value);

        private HashSet<string> Known => Of<HashSet<string>>(_client, "KnownContainers");
        private Dictionary<string, double> Leaving => Of<Dictionary<string, double>>(_client, "RowsLeaving");

        /// <summary>A gateway holding every row in the registry, a welcomed client and its pawn standing in <paramref name="start"/>.</summary>
        private void StartGateway(Container start, Vector3 position)
        {
            _gatewayHost = new GameObject("gateway");
            _gateway = _gatewayHost.AddComponent<NebulaGateway>();
            GatewayType.GetField("<Config>k__BackingField", Flags).SetValue(_gateway, _mesh.Config);
            GatewayType.GetField("_transport", Flags).SetValue(_gateway, new ConformanceMesh.RecordingTransport());
            Call("InitializeInterest");
            var rows = (IDictionary)Field("_ownershipById");
            foreach (var c in ContainerRegistry.Runtime)
                rows[c.ContainerId] = new ContainerOwnershipEntry
                {
                    Instance = c.Instance, ContainerIndex = ContainerRef.RuntimeIndex, ContainerId = c.ContainerId,
                    WorkerId = c.OwnerWorkerId, WorkerIndex = c.OwnerWorkerIndex, Epoch = 1, State = LeaseState.Active,
                };

            var clientType = GatewayType.GetNestedType("ClientConn", BindingFlags.NonPublic);
            _client = Activator.CreateInstance(clientType, true);
            Set(_client, "ClientId", ClientId);
            Set(_client, "PeerId", 7);
            Set(_client, "Welcomed", true);
            Set(_client, "LastScope", start.InstanceId);
            Set(_client, "PawnNetId", PawnNetId);
            ((IDictionary)Field("_clientsById")).Add(ClientId, _client);

            var recordType = GatewayType.GetNestedType("EntityRecord", BindingFlags.NonPublic);
            _pawn = Activator.CreateInstance(recordType, true);
            Set(_pawn, "NetId", PawnNetId);
            Set(_pawn, "OwnerClientId", ClientId);
            Set(_pawn, "OwnerWorkerIndex", (ushort)1);
            ((IDictionary)Field("_entities")).Add(PawnNetId, _pawn);
            MoveTo(start, position);
        }

        /// <summary>Put the pawn in <paramref name="container"/> at an absolute position, as its state entries would, and run the row window.</summary>
        private void MoveTo(Container container, Vector3 position)
        {
            Set(_pawn, "Container", ContainerRef.Runtime(container.RuntimeId));
            Set(_pawn, "AbsX", (double)position.x);
            Set(_pawn, "AbsY", (double)position.y);
            Set(_pawn, "AbsZ", (double)position.z);
            Call("SyncOwnershipWindow", _client);
        }

        /// <summary>Let every row that has been leaving outlast <see cref="NebulaGateway.ContainerRowLingerSeconds"/>, and run the window again.</summary>
        private void OutlastLinger()
        {
            foreach (var id in new List<string>(Leaving.Keys)) Leaving[id] = -1000;
            Call("SyncOwnershipWindow", _client);
        }

        private Vector3 CenterOf(Vector3Int coord) => _host.BoundsOf(coord).center;

        // ------------------------------------------------------------------------------------ the option

        [Test]
        public void ObserveHostIsOffByDefaultAndAbsentFromWhatWasWrittenBefore()
        {
            Assert.IsFalse(new ScopeDefinition().ObserveHost);
            var template = ScriptableObject.CreateInstance<InstanceTemplate>();
            try { Assert.IsFalse(template.ObserveHost); }
            finally { Object.DestroyImmediate(template); }

            var definition = new ScopeDefinition { Parts = { new ScopePart { PartId = "interior", Size = Vector3.one } } };
            StringAssert.DoesNotContain("observeHost", ScopeJson.WriteDefinition(definition), "a definition without it is the JSON it always was");
            var info = new InstanceContainerInfo { InstanceId = 7, ScopeKey = "vault/a", PartId = "interior" };
            var wire = new NetworkWriter();
            info.Write(wire);
            Assert.AreEqual(Convert.ToBase64String(wire.ToArray()), InstanceContainerInfo.Encode(info), "and a lease row without it is the bytes it always was");
        }

        [Test]
        public void ObserveHostTravelsOnTheDefinitionAndTheLeaseRowButNotTheClientWire()
        {
            var definition = new ScopeDefinition { ObserveHost = true, Parts = { new ScopePart { PartId = "interior", Size = Vector3.one } } };
            Assert.IsTrue(ScopeJson.ReadDefinition(ScopeJson.WriteDefinition(definition)).ObserveHost);

            var on = new InstanceContainerInfo { InstanceId = 7, ScopeKey = "vault/a", PartId = "interior", ObserveHost = true };
            var decoded = InstanceContainerInfo.Decode(InstanceContainerInfo.Encode(on));
            Assert.IsTrue(decoded.ObserveHost);
            Assert.AreEqual("interior", decoded.PartId);
            Assert.IsTrue(on.Copy().ObserveHost, "a registry copy keeps it");
            var off = new InstanceContainerInfo { InstanceId = 7, ScopeKey = "vault/a", PartId = "interior" };
            Assert.IsFalse(InstanceContainerInfo.Decode(InstanceContainerInfo.Encode(off)).ObserveHost);

            NetworkWriter a = new NetworkWriter(), b = new NetworkWriter();
            on.Write(a);
            off.Write(b);
            CollectionAssert.AreEqual(b.ToArray(), a.ToArray(), "a client is sent the row it was always sent: only the gateway acts on the option");
        }

        [Test]
        public void ActivatingAScopeWithObserveHostWritesItOnEveryPartsLeaseRow()
        {
            using var plane = new LocalControlPlane();
            plane.Connect();
            plane.RegisterWorker("w-1", 1, "127.0.0.1", 7001);
            plane.HeartbeatWorker("w-1", WorkerStatus.Ready, default);
            plane.ActivateScope(new ScopeActivationRequest
            {
                ScopeKey = "vault/a",
                PreferredWorkerId = "w-1",
                Definition = new ScopeDefinition
                {
                    ObserveHost = true,
                    Parts = { new ScopePart { PartId = "hall", Center = Vector3.zero, Size = Vector3.one * 10 }, new ScopePart { PartId = "cellar", Center = Vector3.down * 10, Size = Vector3.one * 10 } },
                },
            });
            Assert.IsTrue(plane.FindLease(ScopeKeys.ContainerId("vault/a", "hall")).Instance.ObserveHost);
            Assert.IsTrue(plane.FindLease(ScopeKeys.ContainerId("vault/a", "cellar")).Instance.ObserveHost);
        }

        // ------------------------------------------------------------------------------------ the gateway

        [Test]
        public void AnOccupantKeepsTheHostRowsAroundTheBoundaryAndTheyLeaveNormallyAfterward()
        {
            var here = HostChunk(Vector3Int.zero);
            // A neighbour leased to another worker: rows are the control plane's, whoever simulates them.
            var next = HostChunk(new Vector3Int(1, 0, 0), "w-2", 2);
            var deep = HostChunk(new Vector3Int(20, 0, 0));
            var away = HostChunk(new Vector3Int(-20, 0, 0));
            var vault = Instance("a", CenterOf(Vector3Int.zero), observeHost: true);
            var door = CenterOf(Vector3Int.zero);
            StartGateway(here, door);
            CollectionAssert.IsSubsetOf(new[] { here.ContainerId, next.ContainerId }, Known, "the pawn's window in the host");
            var atBoundary = HostRows();

            // Through the boundary: same absolute pose, now in the instance.
            MoveTo(vault, door);
            CollectionAssert.Contains(Known, vault.ContainerId);
            CollectionAssert.AreEquivalent(atBoundary, HostRows(), "the occupant keeps the host's rows it had at the boundary");
            CollectionAssert.IsEmpty(Leaving, "and none of them is leaving");

            // Deep inside, far from the door in absolute terms: the window stays where the pawn left the host.
            MoveTo(vault, CenterOf(new Vector3Int(20, 0, 0)));
            OutlastLinger();
            CollectionAssert.AreEquivalent(atBoundary, HostRows(), "the host window is centred on the boundary, not on the occupant");
            CollectionAssert.DoesNotContain(Known, deep.ContainerId, "so its cost is the window it had there and no more");

            // Out again: nothing to rebuild.
            MoveTo(here, door);
            CollectionAssert.AreEquivalent(atBoundary, HostRows());
            foreach (var id in atBoundary) CollectionAssert.DoesNotContain(Leaving.Keys, id, "no host row is leaving");
            CollectionAssert.Contains(Leaving.Keys, vault.ContainerId, "the instance's row is, as for any scope the pawn has left");

            // Away from both: the rows leave as any row does.
            MoveTo(away, CenterOf(new Vector3Int(-20, 0, 0)));
            CollectionAssert.Contains(Leaving.Keys, here.ContainerId, "a row that left the window lingers first");
            OutlastLinger();
            CollectionAssert.DoesNotContain(Known, here.ContainerId);
            CollectionAssert.DoesNotContain(Known, next.ContainerId);
            CollectionAssert.DoesNotContain(Known, vault.ContainerId);
        }

        [Test]
        public void WithoutObserveHostTheHostRowsLeaveAsBefore()
        {
            var here = HostChunk(Vector3Int.zero);
            var next = HostChunk(new Vector3Int(1, 0, 0), "w-2", 2);
            var vault = Instance("a", CenterOf(Vector3Int.zero), observeHost: false);
            var door = CenterOf(Vector3Int.zero);
            StartGateway(here, door);
            CollectionAssert.IsSubsetOf(new[] { here.ContainerId, next.ContainerId }, Known);

            MoveTo(vault, door);
            CollectionAssert.IsSubsetOf(new[] { here.ContainerId, next.ContainerId }, Leaving.Keys, "an occupant is told none of the host's rows (D10)");
            OutlastLinger();
            CollectionAssert.IsEmpty(HostRows(), "so the client tears the host down once they have lingered");
            CollectionAssert.Contains(Known, vault.ContainerId);
        }

        [Test]
        public void APublicHostIsKeptToo()
        {
            var here = PublicCell(Vector3Int.zero);
            var next = PublicCell(new Vector3Int(1, 0, 0));
            var door = here.WorldBounds.center;
            var vault = Instance("a", door, observeHost: true);
            StartGateway(here, door);
            CollectionAssert.IsSubsetOf(new[] { here.ContainerId, next.ContainerId }, Known);

            MoveTo(vault, door);
            OutlastLinger();
            CollectionAssert.IsSubsetOf(new[] { here.ContainerId, next.ContainerId }, Known, "the public world around the door stays, with ObservePublic off");
        }

        [Test]
        public void ANestedInstanceObservesOnlyItsImmediateHostAndTheChainComesBackOnTheWayOut()
        {
            var here = HostChunk(Vector3Int.zero);
            var far = HostChunk(new Vector3Int(10, 0, 0));
            var door = CenterOf(Vector3Int.zero);
            // The outer instance is long: its hall is at the door, its cellar far inside, where the inner instance's door is.
            var hall = Instance("outer", door, observeHost: true, part: "hall");
            var innerDoor = CenterOf(new Vector3Int(10, 0, 0));
            var cellar = Instance("outer", innerDoor, observeHost: true, part: "cellar");
            var inner = Instance("inner", innerDoor, observeHost: true);
            StartGateway(here, door);

            MoveTo(hall, door);
            CollectionAssert.Contains(Known, here.ContainerId, "the outer instance keeps the host");
            MoveTo(cellar, innerDoor);
            CollectionAssert.Contains(Known, here.ContainerId, "wherever its occupant walks inside it");
            MoveTo(inner, innerDoor);
            OutlastLinger();
            CollectionAssert.Contains(Known, cellar.ContainerId, "the inner instance keeps the outer one around its door");
            CollectionAssert.DoesNotContain(Known, hall.ContainerId, "and only there");
            CollectionAssert.DoesNotContain(Known, here.ContainerId, "but not the outer one's own host: one window, never the chain");
            CollectionAssert.DoesNotContain(Known, far.ContainerId, "nor the host world under the inner door");

            // Back into the outer instance: its host comes back, around the door the pawn first came in by.
            MoveTo(cellar, innerDoor);
            CollectionAssert.Contains(Known, here.ContainerId);
            MoveTo(here, door);
            Assert.AreEqual(0, Of<IList>(_client, "HostViews").Count, "and back in the host, there is nothing left to remember");
        }

        [Test]
        public void AClientThatLeavesForgetsWhereItCameFrom()
        {
            var here = HostChunk(Vector3Int.zero);
            var vault = Instance("a", CenterOf(Vector3Int.zero), observeHost: true);
            StartGateway(here, CenterOf(Vector3Int.zero));
            MoveTo(vault, CenterOf(Vector3Int.zero));
            Assert.AreEqual(1, Of<IList>(_client, "HostViews").Count);
            Call("ForgetClientInterest", _client);
            Assert.AreEqual(0, Of<IList>(_client, "HostViews").Count);
        }

        private List<string> HostRows()
        {
            var rows = new List<string>();
            foreach (var id in Known)
            {
                var c = ContainerRegistry.FindById(id);
                if (c != null && c.InstanceId == _host.InstanceId) rows.Add(id);
            }
            return rows;
        }

        // ------------------------------------------------------------------------------------ the client

        [Test]
        public void AClientHidesAScopedGridsChunksWhileItsPawnIsInAnotherScope()
        {
            NebulaRuntime.IsClient = true;
            NebulaChunks.Activate(_host, NebulaRoles.Client, false, null);
            var chunk = HostChunk(Vector3Int.zero);
            var root = NebulaChunks.RootOf(chunk.RuntimeId);
            Assert.IsNotNull(root);
            var drawn = new GameObject("rock", typeof(MeshRenderer)).GetComponent<MeshRenderer>();
            var off = new GameObject("decal", typeof(MeshRenderer)).GetComponent<MeshRenderer>();
            drawn.transform.SetParent(root, false);
            off.transform.SetParent(root, false);
            off.forceRenderingOff = true;

            InstanceScenes.SetView(_host.InstanceId);
            Assert.IsFalse(drawn.forceRenderingOff, "the pawn's own scope is drawn");

            ulong vault = NebulaWorker.InstanceKey("vault/a");
            InstanceScenes.SetView(vault);
            Assert.IsTrue(drawn.forceRenderingOff, "the host stays resident but is not drawn from inside the instance");
            Assert.IsFalse(NebulaChunks.IsDrawn(new ChunkContext(Vector3Int.zero, chunk.RuntimeId, chunk, root, NebulaRoles.Client, false, _host)));

            var late = new GameObject("late", typeof(MeshRenderer)).GetComponent<MeshRenderer>();
            late.transform.SetParent(root, false);
            InstanceScenes.SetView(NebulaWorker.InstanceKey("vault/b"));
            Assert.IsTrue(late.forceRenderingOff, "content added since is hidden at the next view change");

            InstanceScenes.SetView(_host.InstanceId);
            Assert.IsFalse(drawn.forceRenderingOff, "back in the host it is drawn again");
            Assert.IsFalse(late.forceRenderingOff);
            Assert.IsTrue(off.forceRenderingOff, "and a renderer the game turned off stays off");
        }

        [Test]
        public void AChunkLoadedWhileItsScopeIsNotTheViewArrivesHidden()
        {
            NebulaRuntime.IsClient = true;
            NebulaChunks.Activate(_host, NebulaRoles.Client, false, null);
            InstanceScenes.SetView(NebulaWorker.InstanceKey("vault/a"));
            MeshRenderer rock = null;
            ChunkHandler build = (in ChunkContext chunk) =>
            {
                rock = new GameObject("rock", typeof(MeshRenderer)).GetComponent<MeshRenderer>();
                rock.transform.SetParent(chunk.Root, false);
            };
            NebulaChunks.Loaded += build;
            try
            {
                HostChunk(Vector3Int.zero);
                Assert.IsNotNull(rock);
                Assert.IsTrue(rock.forceRenderingOff);
            }
            finally { NebulaChunks.Loaded -= build; }
        }

        [Test]
        public void ThePublicWorldsChunksAreNeverHidden()
        {
            NebulaRuntime.IsClient = true;
            NebulaChunks.Activate(new RuntimeGrid(Cell, false), NebulaRoles.Client, false, null);
            var cell = PublicCell(Vector3Int.zero);
            var root = NebulaChunks.RootOf(cell.RuntimeId);
            Assert.IsNotNull(root, "the public grid reports its chunk");
            var rock = new GameObject("rock", typeof(MeshRenderer)).GetComponent<MeshRenderer>();
            rock.transform.SetParent(root, false);
            InstanceScenes.SetView(NebulaWorker.InstanceKey("vault/a"));
            Assert.IsFalse(rock.forceRenderingOff, "the public world is drawn from inside an instance, as it always was");
        }
    }
}
