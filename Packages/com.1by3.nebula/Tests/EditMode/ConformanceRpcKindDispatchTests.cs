using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// <b>Conformance: an incoming RPC runs only a method of the kind its message allows.</b> The hash in an RPC message comes off
    /// the wire, and <see cref="RpcRegistry"/> finds a method by hash alone, so each dispatch path says which kind it accepts:
    /// a <c>ServerRpc</c> message reaches only <c>[ServerRpc]</c> methods, an <c>AuthorityRpc</c> message only <c>[AuthorityRpc]</c>,
    /// and an <c>EntityRpc</c> arriving at a client only <c>[ClientRpc]</c>. Before this check the client that owned an entity could
    /// run any RPC body on it (a <c>[ClientRpc]</c> or <c>OwnerRpc</c> body, or an <c>[AuthorityRpc]</c> one) by sending its hash in
    /// a <c>ServerRpc</c>. Each path below is driven through its real handler: the registry, the client's <c>OnEntityRpc</c>,
    /// and a real <see cref="NebulaWorker"/>'s <c>Dispatch</c> for both worker paths. Every test proves the wrong kind is refused
    /// (nothing runs, the drop is counted and logged) and the right kind still runs.
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceRpcKindDispatchTests
    {
        private const ulong Sender = 91;

        private sealed class Probe : NetworkBehaviour
        {
            public int ClientRuns, ServerRuns, AuthorityRuns, RequestRuns;
            public int LastArg;

            [ClientRpc] public void OnClientSide(int x) { ClientRuns++; LastArg = x; }
            [ServerRpc] public void OnOwnerRequest(int x) { ServerRuns++; LastArg = x; }
            [AuthorityRpc] public void OnAuthority(int x) { AuthorityRuns++; LastArg = x; }
            [ClientRequestRpc] public void OnRequest(ClientRequestContext context, int x) { RequestRuns++; LastArg = x; }

            /// <summary>The local path: an AuthorityRpc sent from the worker that holds authority runs in-process.</summary>
            public void CallAuthority(int x) => AuthorityRpc(OnAuthority, x);

            public int Total => ClientRuns + ServerRuns + AuthorityRuns;
        }

        private sealed class NullSink : IRpcSink
        {
            public void SendClientRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, ulong targetClientId, float radius) { }
            public void SendServerRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args) { }
            public void SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args) { }
            public ulong SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, Action<AuthorityCallResult> onDone, float timeoutSeconds) => 0;
        }

        private static readonly uint ClientHash = RpcRegistry.Hash("OnClientSide");
        private static readonly uint ServerHash = RpcRegistry.Hash("OnOwnerRequest");
        private static readonly uint AuthorityHash = RpcRegistry.Hash("OnAuthority");
        private static readonly uint RequestHash = RpcRegistry.Hash("OnRequest");

        private static byte[] IntArg(int x)
        {
            var w = new NetworkWriter();
            w.WriteInt(x);
            return w.ToArray();
        }

        private readonly List<GameObject> _objects = new List<GameObject>();
        private ConformanceMesh _mesh;
        private bool _wasServer, _wasClient;
        private IRpcSink _previousSink;

        [SetUp]
        public void SetUp()
        {
            _wasServer = NebulaRuntime.IsServer;
            _wasClient = NebulaRuntime.IsClient;
            _previousSink = NebulaRuntime.RpcSink;
            NebulaDiagnostics.ResetForNewSession();
        }

        [TearDown]
        public void TearDown()
        {
            _mesh?.Dispose();
            _mesh = null;
            foreach (var o in _objects) if (o != null) Object.DestroyImmediate(o);
            _objects.Clear();
            NebulaDiagnostics.ResetForNewSession();
            NebulaRuntime.IsServer = _wasServer;
            NebulaRuntime.IsClient = _wasClient;
            NebulaRuntime.RpcSink = _previousSink;
        }

        private static void ExpectDrop(string method, RpcKind actual, RpcKind arrivedAs) =>
            LogAssert.Expect(LogType.Warning, new Regex($"RPC Probe\\.{method} is a {actual} RPC and arrived as a {arrivedAs} call; dropped without running"));

        private Probe NewProbe(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            go.AddComponent<NetworkIdentity>();
            return go.AddComponent<Probe>();
        }

        // ------------------------------------------------------------------------------------- the registry

        [Test]
        public void TheRegistryRunsAMethodOnlyForTheKindTheCallerExpects()
        {
            var p = NewProbe("probe");
            // Every kind of method against every kind a message path can expect. ClientRequest is a method kind only: no message
            // path passes it to Invoke (client requests go through InvokeClientRequest), so it is refused on all three.
            var kinds = new[] { RpcKind.Client, RpcKind.Server, RpcKind.Authority, RpcKind.ClientRequest };
            var hashes = new[] { ClientHash, ServerHash, AuthorityHash, RequestHash };
            var names = new[] { "OnClientSide", "OnOwnerRequest", "OnAuthority", "OnRequest" };
            int drops = 0;
            for (int method = 0; method < 4; method++)
            {
                for (int expected = 0; expected < 3; expected++)
                {
                    int before = p.Total;
                    if (method != expected)
                    {
                        drops++;
                        if ((drops & (drops - 1)) == 0) ExpectDrop(names[method], kinds[method], kinds[expected]); // 1st, 2nd, 4th
                    }
                    bool ran = RpcRegistry.Invoke(p, hashes[method], new NetworkReader(new ArraySegment<byte>(IntArg(7))), kinds[expected]);
                    Assert.AreEqual(method == expected, ran, $"a {kinds[method]} method called as {kinds[expected]}");
                    Assert.AreEqual(method == expected ? before + 1 : before, p.Total);
                }
            }
            Assert.AreEqual(9, NebulaDiagnostics.WrongKindRpcsDropped);
            Assert.AreEqual(new[] { 1, 1, 1, 0 }, new[] { p.ClientRuns, p.ServerRuns, p.AuthorityRuns, p.RequestRuns }, "each ran exactly once, for its own kind; the ClientRequest method never ran");
        }

        [Test]
        public void AClientRequestRunsOnlyThroughTheClientRequestPathAndNoOtherKindDoes()
        {
            var p = NewProbe("probe");
            var context = new ClientRequestContext(default(ClientRequestRoutedMsg), null, null);
            foreach (var hash in new[] { ClientHash, ServerHash, AuthorityHash })
                Assert.AreEqual(ClientRequestOutcome.RejectedNoHandler,
                    RpcRegistry.InvokeClientRequest(p, hash, new NetworkReader(new ArraySegment<byte>(IntArg(3))), context));
            Assert.AreEqual(0, p.Total);
            Assert.AreEqual(0, NebulaDiagnostics.WrongKindRpcsDropped, "the client-request path has its own refusal and does not count here");

            Assert.AreEqual(ClientRequestOutcome.Accepted,
                RpcRegistry.InvokeClientRequest(p, RequestHash, new NetworkReader(new ArraySegment<byte>(IntArg(3))), context));
            Assert.AreEqual(1, p.RequestRuns);
            Assert.AreEqual(3, p.LastArg);
        }

        [Test]
        public void WrongKindDropsAreLoggedOnAPowerOfTwoSchedule()
        {
            var p = NewProbe("probe");
            foreach (int n in new[] { 1, 2, 4, 8 }) LogAssert.Expect(LogType.Warning, new Regex($"\\({n} such calls so far\\)"));
            for (int i = 1; i <= 9; i++)
                RpcRegistry.Invoke(p, ClientHash, new NetworkReader(new ArraySegment<byte>(IntArg(1))), RpcKind.Server);
            Assert.AreEqual(9, NebulaDiagnostics.WrongKindRpcsDropped);
            Assert.AreEqual(0, p.Total);
            // LogAssert fails the test on any unexpected warning, so the 3rd, 5th-7th and 9th drops were silent.
        }

        // ------------------------------------------------------------------------------------- the client: EntityRpc

        [Test]
        public void AClientRunsAnEntityRpcOnlyForAClientRpcMethod()
        {
            NebulaRuntime.Reset();
            var host = new GameObject("client");
            _objects.Add(host);
            var client = host.AddComponent<NebulaClient>();
            var p = NewProbe("replica");
            var identity = p.GetComponent<NetworkIdentity>();
            identity.Initialize();
            identity.NetId = 0x7101;
            identity.IsSpawned = true;
            var entities = (IDictionary<ulong, NetworkIdentity>)typeof(NebulaClient).GetField("_entities", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(client);
            entities[identity.NetId] = identity;
            var onEntityRpc = typeof(NebulaClient).GetMethod("OnEntityRpc", BindingFlags.Instance | BindingFlags.NonPublic);
            void Deliver(uint hash) => onEntityRpc.Invoke(client, new object[] { new EntityRpcMsg { NetId = identity.NetId, Epoch = 0, BehaviourIndex = 0, MethodHash = hash, Args = IntArg(5) } });

            // The bodies that belong to the server never run on a client, however the hash got there.
            ExpectDrop("OnOwnerRequest", RpcKind.Server, RpcKind.Client);
            Deliver(ServerHash);
            ExpectDrop("OnAuthority", RpcKind.Authority, RpcKind.Client);
            Deliver(AuthorityHash);
            Assert.AreEqual(0, p.Total, "neither ran");
            Assert.AreEqual(2, NebulaDiagnostics.WrongKindRpcsDropped);

            Deliver(ClientHash);
            Assert.AreEqual(1, p.ClientRuns, "the [ClientRpc] method still runs");
            Assert.AreEqual(5, p.LastArg);
            Assert.AreEqual(1, p.Total);
            NebulaRuntime.Reset();
        }

        // ------------------------------------------------------------------------------------- the worker

        private (NetworkIdentity Entity, Probe Probe, ConformanceMesh.Gateway Gateway) SpawnOnFirstWorker()
        {
            _mesh = new ConformanceMesh(2);
            var gateway = _mesh.AddGateway("g1");
            _mesh.LinkGateway(gateway);
            var outdoor = _mesh.AddStaticContainer("kind-outdoor", Vector3.zero, new Vector3(400, 60, 400));
            var prefab = new GameObject("kind-probe-prefab");
            _objects.Add(prefab);
            prefab.AddComponent<NetworkIdentity>();
            prefab.AddComponent<Probe>();
            ushort prefabId = _mesh.RegisterPrefab(prefab);
            var w1 = _mesh[0];
            NetworkIdentity e = null;
            w1.Act(() =>
            {
                e = NetworkPrefabs.Instantiate(prefabId, new Vector3(5f, 1f, 5f), Quaternion.identity, outdoor.transform);
                w1.Instance.Spawn(e, outdoor, 0);
                e.OwnerClientId = Sender;
            });
            Assert.IsTrue(w1.Find(e.NetId).HasAuthority);
            return (e, e.GetComponent<Probe>(), gateway);
        }

        [Test]
        public void AWorkerRunsAServerRpcMessageOnlyForAServerRpcMethod()
        {
            var (e, p, gateway) = SpawnOnFirstWorker();
            var w1 = _mesh[0];
            void Send(uint hash) => _mesh.FromGateway(gateway, w1, w => new EntityRpcMsg { NetId = e.NetId, Epoch = e.Epoch, BehaviourIndex = 0, MethodHash = hash, ClientId = Sender, Args = IntArg(9) }.Write(w, MsgId.ServerRpc));

            // The owner names the bodies that should only run on clients, or only through an authority call.
            ExpectDrop("OnClientSide", RpcKind.Client, RpcKind.Server);
            Send(ClientHash);
            ExpectDrop("OnAuthority", RpcKind.Authority, RpcKind.Server);
            Send(AuthorityHash);
            Assert.AreEqual(0, p.Total, "no wrong-kind body ran");
            Assert.AreEqual(2, NebulaDiagnostics.WrongKindRpcsDropped);

            Send(ServerHash);
            Assert.AreEqual(1, p.ServerRuns, "the [ServerRpc] method still runs");
            Assert.AreEqual(9, p.LastArg);
            Assert.AreEqual(1, p.Total);
        }

        [Test]
        public void AWorkerRunsAnAuthorityRpcMessageOnlyForAnAuthorityRpcMethod()
        {
            var (e, p, _) = SpawnOnFirstWorker();
            var w1 = _mesh[0];
            var sender = new AuthorityCallTracker(2);
            // A worker builds its call router when it starts; the mesh harness does not start one.
            typeof(NebulaWorker).GetField("_callRouter", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(w1.Instance, new AuthorityCallRouter(3));
            void Send(uint hash)
            {
                var w = new NetworkWriter(64);
                new AuthorityCallMsg
                {
                    CallId = sender.Mint(), Hops = 0, Flags = AuthorityCallFlags.None, NetId = e.NetId, Epoch = e.Epoch,
                    BehaviourIndex = 0, MethodHash = hash, Args = IntArg(3),
                }.Write(w);
                w1.Dispatch(w1.PeersById["w2"], w.ToArray());
            }

            ExpectDrop("OnClientSide", RpcKind.Client, RpcKind.Authority);
            Send(ClientHash);
            ExpectDrop("OnOwnerRequest", RpcKind.Server, RpcKind.Authority);
            Send(ServerHash);
            Assert.AreEqual(0, p.Total, "no wrong-kind body ran");
            Assert.AreEqual(2, NebulaDiagnostics.WrongKindRpcsDropped);

            Send(AuthorityHash);
            Assert.AreEqual(1, p.AuthorityRuns, "the [AuthorityRpc] method still runs");
            Assert.AreEqual(3, p.LastArg);
            Assert.AreEqual(1, p.Total);
        }

        [Test]
        public void ALocalAuthorityRpcOnTheAuthorityStillRuns()
        {
            var (e, p, _) = SpawnOnFirstWorker();
            NebulaRuntime.RpcSink = new NullSink();
            _mesh[0].Act(() => p.CallAuthority(4));
            Assert.AreEqual(1, p.AuthorityRuns);
            Assert.AreEqual(4, p.LastArg);
            Assert.AreEqual(0, NebulaDiagnostics.WrongKindRpcsDropped);
        }
    }
}
