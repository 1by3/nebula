using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// <b>Conformance: client requests, the worker half</b> (<c>docs/client-requests.md</c>, FND-50), tier B: two real
    /// <see cref="NebulaWorker"/>s on the <see cref="ConformanceMesh"/> and its recording gateway. A gateway-stamped request is applied by
    /// the worker with authority and the handler receives the stamp (client id, identity, claims, pawn, position, hops); one that arrives
    /// at the worker holding only a ghost is passed on and applied once at the owner, with the reply going back to the gateway; and what
    /// a client can ask for is exactly the <c>[ClientRequestRpc]</c> methods: a <c>[ServerRpc]</c> by its hash, an unknown entity, a
    /// bad argument list, an oversize payload, a handler that throws, and a request that has been forwarded too often are each rejected
    /// with their reason, and none runs a method. The gateway half is <c>ConformanceClientRequestGatewayTests</c> in the .NET suite.
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformanceClientRequestWorkerTests
    {
        private const ulong Sender = 91;

        private sealed class RequestProbe : NetworkBehaviour
        {
            public readonly List<(ClientRequestContext Context, int Amount, string Note)> Pokes = new List<(ClientRequestContext, int, string)>();
            public int OwnerOnlyCalls;

            [ClientRequestRpc]
            public void Poke(ClientRequestContext context, int amount, string note)
            {
                Pokes.Add((context, amount, note));
                context.ReplyCode = 42;
            }

            [ClientRequestRpc]
            public void Explode(ClientRequestContext context) => throw new InvalidOperationException("boom");

            [ServerRpc]
            public void OwnerOnly(int x) => OwnerOnlyCalls++;

            // Sending side, called on a client.
            public uint SendPoke(int amount, string note, Action<ClientRequestResult> onDone = null) =>
                onDone == null ? Send(amount, note) : ClientRequestWithReply(Poke, amount, note, onDone);
            private uint Send(int amount, string note) { ClientRequest(Poke, amount, note); return 0; }
        }

        private ConformanceMesh _mesh;
        private ConformanceMesh.Gateway _gateway;
        private Container _outdoor;
        private ushort _prefabId;
        private bool _wasServer, _wasClient;
        private IRpcSink _previousSink;
        private readonly List<GameObject> _objects = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _wasServer = NebulaRuntime.IsServer;
            _wasClient = NebulaRuntime.IsClient;
            _previousSink = NebulaRuntime.RpcSink;
            _mesh = new ConformanceMesh(2);
            _gateway = _mesh.AddGateway("g1");
            _mesh.LinkGateway(_gateway);
            _outdoor = _mesh.AddStaticContainer("requests-outdoor", Vector3.zero, new Vector3(400, 60, 400));
            var prefab = new GameObject("request-probe-prefab");
            prefab.AddComponent<NetworkIdentity>();
            prefab.AddComponent<RequestProbe>();
            _prefabId = _mesh.RegisterPrefab(prefab);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _objects) if (o != null) Object.DestroyImmediate(o);
            _objects.Clear();
            _mesh.Dispose();
            NebulaRuntime.IsServer = _wasServer;
            NebulaRuntime.IsClient = _wasClient;
            NebulaRuntime.RpcSink = _previousSink;
        }

        /// <summary>An entity spawned on the first worker and handed to the second: the second has authority, the first a ghost.</summary>
        private NetworkIdentity EntityOnSecondWorker()
        {
            var w1 = _mesh[0];
            NetworkIdentity e = null;
            w1.Act(() =>
            {
                e = NetworkPrefabs.Instantiate(_prefabId, new Vector3(5f, 1f, 5f), Quaternion.identity, _outdoor.transform);
                w1.Instance.Spawn(e, _outdoor, 0);
            });
            w1.Transfer(e, _mesh[1]);
            _mesh.Pump();
            Assert.IsTrue(_mesh[1].Find(e.NetId).HasAuthority, "the second worker owns it");
            Assert.IsFalse(w1.Find(e.NetId).HasAuthority, "the first holds a ghost");
            return e;
        }

        private static RequestProbe ProbeOn(ConformanceMesh.Worker worker, ulong netId) => worker.Find(netId).GetComponent<RequestProbe>();

        private static ClientRequestRoutedMsg Request(ulong netId, uint method, byte[] args = null, byte hops = 0, bool reply = true) => new ClientRequestRoutedMsg
        {
            ClientId = Sender,
            Identity = "alice-identity",
            Claims = PlayerClaims.From(new Dictionary<string, string> { ["role"] = "builder" }),
            PawnNetId = 77,
            HasPawnPosition = true,
            PawnPosition = new Vector3(10, 1, 10),
            CallId = 5,
            Flags = reply ? ClientRequestFlags.WantsReply : ClientRequestFlags.None,
            Hops = hops,
            NetId = netId,
            BehaviourIndex = 0,
            MethodHash = method,
            Args = args ?? Array.Empty<byte>(),
        };

        private static byte[] PokeArgs(int amount, string note)
        {
            var w = new NetworkWriter();
            w.WriteInt(amount);
            w.WriteString(note);
            return w.ToArray();
        }

        private void Send(ConformanceMesh.Worker to, ClientRequestRoutedMsg msg) => _mesh.FromGateway(_gateway, to, w => msg.Write(w));

        private List<ClientRequestReplyMsg> Replies()
        {
            _mesh.Pump();
            var replies = new List<ClientRequestReplyMsg>();
            foreach (var m in _mesh.DeliveredOf(MsgId.ClientRequestReply, _gateway.Id)) replies.Add(m.Read(ClientRequestReplyMsg.Read));
            return replies;
        }

        // ------------------------------------------------------------------------------------- the registry

        [Test]
        public void OnlyClientRequestMethodsAreReachableAsClientRequestsAndTheirContextIsNotOnTheWire()
        {
            var poke = RpcRegistry.Require(typeof(RequestProbe), "Poke");
            Assert.AreEqual(RpcKind.ClientRequest, poke.Kind);
            Assert.IsTrue(poke.TakesContext);
            CollectionAssert.AreEqual(new[] { typeof(int), typeof(string) }, poke.ParameterTypes, "the context is not a serialised parameter");

            var go = new GameObject("probe");
            _objects.Add(go);
            go.AddComponent<NetworkIdentity>().Initialize();
            var probe = go.AddComponent<RequestProbe>();
            var context = new ClientRequestContext(Request(1, 0), null, null);
            Assert.AreEqual(ClientRequestOutcome.RejectedNoHandler,
                RpcRegistry.InvokeClientRequest(probe, RpcRegistry.Hash("OwnerOnly"), new NetworkReader(PokeArgs(1, "")), context),
                "a client cannot name a ServerRpc by its hash");
            Assert.AreEqual(ClientRequestOutcome.RejectedNoHandler, RpcRegistry.InvokeClientRequest(probe, 0xDEAD, new NetworkReader(Array.Empty<byte>()), context));
            Assert.AreEqual(0, probe.OwnerOnlyCalls);

            // And the other way: the old paths cannot reach a client request (its first parameter is the sender, which they cannot supply).
            LogAssert.Expect(LogType.Warning, new Regex("cannot be called as another kind"));
            RpcRegistry.Invoke(probe, poke.Hash, new NetworkReader(PokeArgs(1, "x")));
            Assert.AreEqual(0, probe.Pokes.Count);
        }

        private sealed class NoContext : NetworkBehaviour
        {
            [ClientRequestRpc] public void Bad(int x) { }
        }

        [Test]
        public void AClientRequestMethodWithoutTheContextIsRefusedWhenTheTableIsBuilt()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => RpcRegistry.Lookup(typeof(NoContext), RpcRegistry.Hash("Bad")));
            StringAssert.Contains("ClientRequestContext", ex.Message);
        }

        // ------------------------------------------------------------------------------------- the client's send

        private sealed class RecordingSink : IRpcSink
        {
            public readonly List<(ulong NetId, byte Behaviour, uint Method, byte[] Args, bool WantsReply, float Timeout)> Requests = new List<(ulong, byte, uint, byte[], bool, float)>();
            public void SendClientRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, ulong targetClientId, float radius) { }
            public void SendServerRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args) { }
            public void SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args) { }
            public ulong SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, Action<AuthorityCallResult> onDone, float timeoutSeconds) => 0;
            public uint SendClientRequest(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, Action<ClientRequestResult> onDone, float timeoutSeconds)
            {
                var copy = new byte[args.Count];
                Buffer.BlockCopy(args.Array, args.Offset, copy, 0, args.Count);
                Requests.Add((identity.NetId, behaviourIndex, methodHash, copy, onDone != null, timeoutSeconds));
                return 3;
            }
        }

        private RequestProbe SpawnedOnClient()
        {
            var go = new GameObject("client-probe");
            _objects.Add(go);
            var identity = go.AddComponent<NetworkIdentity>();
            var probe = go.AddComponent<RequestProbe>();
            identity.Initialize();
            identity.NetId = 0x5151;
            identity.IsSpawned = true;
            identity.HasAuthority = false;
            identity.OwnerClientId = 1234; // somebody else's: a client request does not need ownership
            NebulaRuntime.IsServer = false;
            NebulaRuntime.IsClient = true;
            return probe;
        }

        [Test]
        public void AClientSendsARequestToAnEntityItDoesNotOwnAndHearsTheReplyOnce()
        {
            var probe = SpawnedOnClient();
            var sink = new RecordingSink();
            NebulaRuntime.RpcSink = sink;

            probe.SendPoke(5, "hello");
            var r = sink.Requests[0];
            Assert.AreEqual(0x5151UL, r.NetId);
            Assert.AreEqual(RpcRegistry.Hash("Poke"), r.Method);
            Assert.IsFalse(r.WantsReply);
            var args = new NetworkReader(r.Args);
            Assert.AreEqual((5, "hello"), (args.ReadInt(), args.ReadString()), "only the arguments after the context are on the wire");

            ClientRequestResult? seen = null;
            Assert.AreEqual(3u, probe.SendPoke(6, "again", result => seen = result));
            Assert.IsTrue(sink.Requests[1].WantsReply);
            Assert.AreEqual(NetworkBehaviour.DefaultClientRequestTimeoutSeconds, sink.Requests[1].Timeout);
            Assert.IsFalse(seen.HasValue);
        }

        [Test]
        public void AClientRequestSentWhereItCannotLeaveSettlesItsCallbackAtOnceWithTheReason()
        {
            var probe = SpawnedOnClient();
            NebulaRuntime.IsClient = false;
            NebulaRuntime.IsServer = true;
            NebulaRuntime.RpcSink = new RecordingSink();
            ClientRequestResult? seen = null;
            LogAssert.Expect(LogType.Warning, new Regex("can only be sent from a client"));
            Assert.AreEqual(0u, probe.SendPoke(1, "x", result => seen = result));
            Assert.AreEqual(ClientRequestOutcome.Disconnected, seen.Value.Outcome);
            Assert.IsFalse(seen.Value.Succeeded);
        }

        [Test]
        public void ASinkWrittenBeforeClientRequestsStillCompilesAndRefusesThem()
        {
            var probe = SpawnedOnClient();
            NebulaRuntime.RpcSink = new LegacyOnlySink();
            ClientRequestResult? seen = null;
            LogAssert.Expect(LogType.Warning, new Regex("can only be sent from a client"));
            probe.SendPoke(1, "x", result => seen = result);
            Assert.AreEqual(ClientRequestOutcome.Disconnected, seen.Value.Outcome);
        }

        private sealed class LegacyOnlySink : IRpcSink
        {
            public void SendClientRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, ulong targetClientId, float radius) { }
            public void SendServerRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args) { }
            public void SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args) { }
            public ulong SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, Action<AuthorityCallResult> onDone, float timeoutSeconds) => 0;
        }

        // ------------------------------------------------------------------------------------- the worker

        [Test]
        public void TheWorkerWithAuthorityRunsTheHandlerOnceWithTheStampAndRepliesToTheGateway()
        {
            var e = EntityOnSecondWorker();
            Send(_mesh[1], Request(e.NetId, RpcRegistry.Hash("Poke"), PokeArgs(7, "note")));

            var pokes = ProbeOn(_mesh[1], e.NetId).Pokes;
            Assert.AreEqual(1, pokes.Count, "ran once");
            var (context, amount, note) = pokes[0];
            Assert.AreEqual((7, "note"), (amount, note));
            Assert.AreEqual(Sender, context.ClientId);
            Assert.AreEqual("alice-identity", context.Identity);
            Assert.AreEqual("builder", context.Claims["role"]);
            Assert.AreEqual(77UL, context.SenderPawnNetId);
            Assert.IsTrue(context.HasSenderPosition);
            Assert.AreEqual(new Vector3(10, 1, 10), context.SenderPosition);
            Assert.AreEqual(5u, context.CallId);
            Assert.AreEqual(0, context.Hops);
            Assert.AreSame(_mesh[1].Find(e.NetId), context.Entity, "the entity is the authoritative copy");
            Assert.IsTrue(context.WantsReply);
            Assert.IsFalse(context.TryGetSenderPawn(out _), "the sender's pawn is on no worker here: the stamp is what a range check uses");
            Assert.AreEqual(0, ProbeOn(_mesh[0], e.NetId).Pokes.Count, "the ghost's copy did not run it");

            var replies = Replies();
            Assert.AreEqual(1, replies.Count);
            Assert.AreEqual((Sender, 5u, ClientRequestOutcome.Accepted, (ushort)42), (replies[0].ClientId, replies[0].CallId, replies[0].Outcome, replies[0].Code), "the handler's own code rides in the reply");
        }

        [Test]
        public void ARequestThatReachesTheGhostIsPassedOnAndAppliedOnceAtTheOwner()
        {
            var e = EntityOnSecondWorker();
            Send(_mesh[0], Request(e.NetId, RpcRegistry.Hash("Poke"), PokeArgs(1, "seam")));
            _mesh.Pump();

            Assert.AreEqual(0, ProbeOn(_mesh[0], e.NetId).Pokes.Count, "the ghost does not run it");
            var pokes = ProbeOn(_mesh[1], e.NetId).Pokes;
            Assert.AreEqual(1, pokes.Count, "the owner runs it once");
            Assert.AreEqual(1, pokes[0].Context.Hops, "and knows it was passed on");
            Assert.AreEqual(Sender, pokes[0].Context.ClientId, "the stamp survives the hop");
            Assert.AreEqual("alice-identity", pokes[0].Context.Identity);
            Assert.AreEqual(1, _mesh[0].Instance.ClientRequestsForwarded);
            Assert.AreEqual(1, _mesh[1].Instance.ClientRequestsApplied);
            var replies = Replies();
            Assert.AreEqual(1, replies.Count, "a worker that was not spoken to by the gateway answers through the gateways it is linked to");
            Assert.AreEqual(ClientRequestOutcome.Accepted, replies[0].Outcome);
        }

        [Test]
        public void ARequestForAnEntityNoWorkerHasIsRejectedWithItsReason()
        {
            Send(_mesh[0], Request(0xFFFF_FFFF, RpcRegistry.Hash("Poke"), PokeArgs(1, "x")));
            Assert.AreEqual(ClientRequestOutcome.RejectedUnknownEntity, Replies()[0].Outcome);
            Assert.AreEqual(1, _mesh[0].Instance.ClientRequestsRejected[(int)ClientRequestOutcome.RejectedUnknownEntity]);
        }

        [Test]
        public void AClientCannotReachAServerRpcABadArgumentListOrAnOversizePayloadAndNoneOfThemRun()
        {
            var e = EntityOnSecondWorker();
            var probe = ProbeOn(_mesh[1], e.NetId);

            Send(_mesh[1], Request(e.NetId, RpcRegistry.Hash("OwnerOnly"), PokeArgs(1, "")));
            Send(_mesh[1], Request(e.NetId, RpcRegistry.Hash("Poke"), new byte[] { 1 })); // an int needs four bytes
            Send(_mesh[1], Request(e.NetId, RpcRegistry.Hash("Poke"), new byte[4000]));
            var bad = Request(e.NetId, RpcRegistry.Hash("Poke"), PokeArgs(1, "x"));
            bad.BehaviourIndex = 9; // a behaviour the entity does not have
            Send(_mesh[1], bad);

            Assert.AreEqual(0, probe.OwnerOnlyCalls, "a ServerRpc is not reachable by a client request");
            Assert.AreEqual(0, probe.Pokes.Count);
            var outcomes = new List<ClientRequestOutcome>();
            foreach (var r in Replies()) outcomes.Add(r.Outcome);
            CollectionAssert.AreEqual(new[]
            {
                ClientRequestOutcome.RejectedNoHandler, ClientRequestOutcome.RejectedBadArguments,
                ClientRequestOutcome.RejectedTooLarge, ClientRequestOutcome.RejectedNoHandler,
            }, outcomes);
        }

        [Test]
        public void AHandlerThatThrowsIsReportedAndDoesNotBreakTheWorker()
        {
            var e = EntityOnSecondWorker();
            LogAssert.Expect(LogType.Error, new Regex("threw"));
            Send(_mesh[1], Request(e.NetId, RpcRegistry.Hash("Explode")));
            Assert.AreEqual(ClientRequestOutcome.HandlerThrew, Replies()[0].Outcome);

            Send(_mesh[1], Request(e.NetId, RpcRegistry.Hash("Poke"), PokeArgs(2, "after")));
            Assert.AreEqual(1, ProbeOn(_mesh[1], e.NetId).Pokes.Count, "the next request runs");
        }

        [Test]
        public void ARequestPassedOnTooOftenIsRejectedAtTheHopLimit()
        {
            var e = EntityOnSecondWorker();
            int maxHops = _mesh.Config.AuthorityCallMaxHops;
            Send(_mesh[0], Request(e.NetId, RpcRegistry.Hash("Poke"), PokeArgs(1, "x"), hops: (byte)maxHops));
            Assert.AreEqual(ClientRequestOutcome.RejectedHopLimit, Replies()[0].Outcome);
            Assert.AreEqual(0, ProbeOn(_mesh[1], e.NetId).Pokes.Count);
        }

        [Test]
        public void ARequestThatAsksForNoReplyGetsNoneWhateverBecomesOfIt()
        {
            var e = EntityOnSecondWorker();
            Send(_mesh[1], Request(e.NetId, RpcRegistry.Hash("Poke"), PokeArgs(1, "x"), reply: false));
            Send(_mesh[1], Request(0xFFFF_FFFF, RpcRegistry.Hash("Poke"), PokeArgs(1, "x"), reply: false));
            Assert.AreEqual(1, ProbeOn(_mesh[1], e.NetId).Pokes.Count);
            Assert.AreEqual(0, Replies().Count);
        }

        [Test]
        public void ARequestFromAnythingButAGatewayOrAWorkerIsIgnored()
        {
            var e = EntityOnSecondWorker();
            var peerType = typeof(NebulaWorker).GetNestedType("Peer", System.Reflection.BindingFlags.NonPublic);
            var peer = Activator.CreateInstance(peerType, true);
            peerType.GetField("Role").SetValue(peer, PeerRole.Client);
            peerType.GetField("HelloReceived").SetValue(peer, true);
            var w = new NetworkWriter(64);
            Request(e.NetId, RpcRegistry.Hash("Poke"), PokeArgs(1, "x")).Write(w);
            _mesh[1].Dispatch(peer, w.ToArray());
            Assert.AreEqual(0, ProbeOn(_mesh[1], e.NetId).Pokes.Count);
        }
    }
}
