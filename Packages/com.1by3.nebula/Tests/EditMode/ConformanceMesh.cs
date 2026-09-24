using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// The in-process mesh the conformance suite runs its worker-side scenarios on (see
    /// <c>docs/conformance-suite.md</c>, tier B): two or more <b>real</b> <see cref="NebulaWorker"/> components, each
    /// with a recording transport in place of a socket, fully connected to each other as worker peers. Every byte a
    /// worker sends is what the production code decided to send; <see cref="Pump"/> delivers it into the receiving
    /// worker's own <c>Dispatch</c>, so a handover here runs the same builder, the same wire format and the same
    /// applier a live mesh runs, deterministically and in one Editor process.
    /// <para>
    /// What it does not have: a real gateway, a control plane, leases, or a tick loop. Handovers are triggered
    /// directly (<see cref="Worker.Transfer"/>), or by one whole tick the scenario asks for (<see cref="Worker.Tick"/>)
    /// against the owners <see cref="SetOwner"/> handed out. A scenario can speak for a gateway
    /// (<see cref="AddGateway"/>, <see cref="FromGateway"/>): what the workers send it is recorded and never
    /// answered, and nothing is announced to it by interest because its link is not registered. Scenarios that need
    /// more belong in the service tests
    /// (tier A, <c>Nebula.Services.Tests/Fixtures/MeshFixtures.cs</c>) or in the later multi-process tier.
    /// </para>
    /// <para>
    /// Private members of the worker are reached by reflection, as <see cref="WorkerHandoverTests"/> does, rather
    /// than by widening the worker's surface: <c>TransferAuthority</c>, <c>Dispatch</c> and the nested <c>Peer</c>
    /// type. A rename there fails these tests loudly at the reflection call, not silently.
    /// </para>
    /// </summary>
    public sealed class ConformanceMesh : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type PeerType = typeof(NebulaWorker).GetNestedType("Peer", BindingFlags.NonPublic);
        private static readonly MethodInfo DispatchMethod = typeof(NebulaWorker).GetMethod("Dispatch", Flags);
        private static readonly MethodInfo TransferMethod = typeof(NebulaWorker).GetMethod("TransferAuthority", Flags);
        private static readonly MethodInfo GhostBandMethod = typeof(NebulaWorker).GetMethod("UpdateGhostBand", Flags);
        private static readonly MethodInfo TickMethod = typeof(NebulaWorker).GetMethod("Tick", Flags);
        /// <summary>Peer ids are global across the mesh: worker index <c>i</c> is peer <c>100 + i</c> on every other worker.</summary>
        private const int PeerIdBase = 100;
        /// <summary>Gateway <c>i</c> (from 1, in the order <see cref="AddGateway"/> made them) is peer <c>900 + i</c> on every worker.</summary>
        private const int GatewayPeerIdBase = 900;
        private const int MaxPumpRounds = 64;

        /// <summary>One message a worker handed its transport, decoded far enough to tell what it was.</summary>
        public readonly struct WireMessage
        {
            public readonly string From;
            public readonly string To;
            public readonly MsgId Id;
            public readonly byte[] Bytes;

            public WireMessage(string from, string to, byte[] bytes)
            {
                From = from; To = to; Bytes = bytes;
                Id = bytes.Length > 0 ? (MsgId)bytes[0] : (MsgId)0;
            }

            /// <summary>Parse the body with the message's own reader, after the id byte.</summary>
            public T Read<T>(Func<NetworkReader, T> read)
            {
                var r = new NetworkReader(Bytes);
                r.ReadByte();
                return read(r);
            }
        }

        /// <summary>A transport that keeps what the worker handed it, so the mesh can deliver it.</summary>
        internal sealed class RecordingTransport : ITransport
        {
            public readonly Queue<KeyValuePair<int, byte[]>> Outbox = new Queue<KeyValuePair<int, byte[]>>();

            public string Name => "conformance";
            public bool IsRunning => true;
            public int LocalPort => 0;
            public void Listen(int port) { }
            public void StartClient() { }
            public int Connect(string host, int port) => 0;
            public void Disconnect(int peerId) { }
            public bool IsConnected(int peerId) => true;
            public int RoundTripMs(int peerId) => 0;

            public void Send(int peerId, Delivery delivery, ArraySegment<byte> payload)
            {
                var copy = new byte[payload.Count];
                Array.Copy(payload.Array, payload.Offset, copy, 0, payload.Count);
                Outbox.Enqueue(new KeyValuePair<int, byte[]>(peerId, copy));
            }

            public void Poll(Action<TransportEvent> handler) { }
            public void Flush() { }
            public void Stop() { }
            public void Dispose() { }
        }

        /// <summary>
        /// A gateway as the workers hear it: a peer with a gateway's role and session key that a scenario speaks for
        /// through <see cref="FromGateway"/>. What the workers send it lands in <see cref="Delivered"/> and goes no
        /// further.
        /// </summary>
        public sealed class Gateway
        {
            public readonly string Id;
            /// <summary>Its session key (<see cref="PlayerSessions.GatewayKey"/>), the one a worker records for a session this gateway speaks for.</summary>
            public readonly string Key;
            internal readonly int PeerId;

            internal Gateway(string id, string key, int peerId) { Id = id; Key = key; PeerId = peerId; }
        }

        /// <summary>One real worker of the mesh and the handles a scenario drives it through.</summary>
        public sealed class Worker
        {
            public readonly NebulaWorker Instance;
            public readonly string Id;
            public readonly ushort Index;
            internal readonly RecordingTransport Transport;
            /// <summary>This worker's Peer record for each other worker and each gateway, by its id.</summary>
            internal readonly Dictionary<string, object> PeersById = new Dictionary<string, object>();
            private readonly ConformanceMesh _mesh;

            internal Worker(ConformanceMesh mesh, GameObject host, string id, ushort index, NebulaConfig config)
            {
                _mesh = mesh;
                Id = id;
                Index = index;
                Instance = host.AddComponent<NebulaWorker>();
                Transport = new RecordingTransport();
                SetProperty("WorkerId", id);
                SetProperty("WorkerIndex", index);
                SetProperty("Config", config);
                SetField("_transport", Transport);
                SetField("_interestGrid", InterestGrid.Resolve(InterestSettings.Default));
            }

            /// <summary>Run worker-side code as this worker: the process-wide "which worker am I" statics point here.</summary>
            public void Act(Action action)
            {
                NebulaRuntime.LocalWorkerIndex = Index;
                NebulaRuntime.LocalWorkerId = Id;
                action();
            }

            /// <summary>
            /// Instantiate a registered prefab under <paramref name="container"/> and spawn it server-driven through
            /// the worker's public <see cref="NebulaWorker.SpawnServerDriven"/>.
            /// </summary>
            public NetworkIdentity SpawnServerDriven(ushort prefabId, Container container, Vector3 position, Quaternion rotation)
            {
                NetworkIdentity identity = null;
                Act(() =>
                {
                    identity = NetworkPrefabs.Instantiate(prefabId, position, rotation, container.transform);
                    Instance.SpawnServerDriven(identity, container);
                });
                return identity;
            }

            /// <summary>
            /// Hand <paramref name="entity"/> to <paramref name="target"/> through the worker's own
            /// <c>TransferAuthority</c>: the exact builder a live worker runs when the tick finds an entity in a
            /// container another worker leases. The bytes wait in this worker's outbox until <see cref="Pump"/>.
            /// </summary>
            public void Transfer(NetworkIdentity entity, Worker target)
            {
                if (!PeersById.TryGetValue(target.Id, out var peer)) throw new ArgumentException($"{target.Id} is not a peer of {Id}");
                Act(() => Invoke(TransferMethod, entity, peer));
            }

            /// <summary>
            /// The authority half of one tick, in the order <see cref="NebulaWorker"/> runs it: record every
            /// authoritative entity's state for <paramref name="tick"/>, prepare its replication entry, then run the
            /// worker's own ghost band, which spawns ghosts on the neighbouring owners and streams this tick's state
            /// to them. The bytes wait in the outbox until <see cref="Pump"/>. There is still no tick loop: the
            /// scenario decides when a tick happens and what moved before it.
            /// </summary>
            public void PublishTick(uint tick)
            {
                Act(() =>
                {
                    foreach (var e in Instance.Entities)
                    {
                        if (e == null || !e.HasAuthority) continue;
                        e.RecordAuthoritativeState(tick);
                        e.PrepareReplication(tick);
                    }
                    Invoke(GhostBandMethod, tick);
                });
            }

            /// <summary>
            /// Run one whole tick of the worker, exactly as its frame loop would: ghosts, simulation by nesting depth,
            /// container membership against the owners <see cref="SetOwner"/> gave out (handing an entity to the
            /// owner of the container it resolved into), the ghost band and the interest pass. The scenario still
            /// decides when a tick happens; the bytes wait in the outbox until <see cref="Pump"/>.
            /// </summary>
            public void Tick(uint tick) => Act(() => Invoke(TickMethod, tick));

            /// <summary>The entity this worker holds under <paramref name="netId"/> (authoritative or ghost), or null.</summary>
            public NetworkIdentity Find(ulong netId) => Instance.Find(netId);

            internal void Dispatch(object fromPeer, byte[] bytes)
            {
                Act(() => Invoke(DispatchMethod, fromPeer, new NetworkReader(bytes)));
            }

            private void Invoke(MethodInfo method, params object[] args)
            {
                try { method.Invoke(Instance, args); }
                // Rethrown with its own stack, so a failure inside the worker points at the line that threw.
                catch (TargetInvocationException ex) when (ex.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); }
            }

            private void SetField(string name, object value) => typeof(NebulaWorker).GetField(name, Flags).SetValue(Instance, value);
            private void SetProperty(string name, object value) => typeof(NebulaWorker).GetProperty(name).SetValue(Instance, value);
            internal object GetField(string name) => typeof(NebulaWorker).GetField(name, Flags).GetValue(Instance);
        }

        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<Worker> _workers = new List<Worker>();
        private readonly Dictionary<int, Worker> _byPeerId = new Dictionary<int, Worker>();
        private readonly List<GameObject> _prefabs = new List<GameObject>();
        private readonly Dictionary<int, Gateway> _gatewaysByPeerId = new Dictionary<int, Gateway>();

        /// <summary>Every message delivered so far, in delivery order.</summary>
        public readonly List<WireMessage> Delivered = new List<WireMessage>();
        public IReadOnlyList<Worker> Workers => _workers;

        /// <summary>The <see cref="NebulaConfig"/> every worker of the mesh was given; defaults, until a scenario changes a field.</summary>
        public NebulaConfig Config { get; }

        /// <summary>Stand up <paramref name="workerCount"/> workers <c>w1..wN</c>, each connected to every other.</summary>
        public ConformanceMesh(int workerCount)
        {
            if (workerCount < 1) throw new ArgumentOutOfRangeException(nameof(workerCount));
            NebulaRuntime.Reset();
            NebulaRuntime.IsServer = true;
            ContainerRegistry.Rebuild();
            Config = ScriptableObject.CreateInstance<NebulaConfig>();
            for (int i = 1; i <= workerCount; i++)
            {
                var host = new GameObject($"worker-w{i}");
                _objects.Add(host);
                var worker = new Worker(this, host, $"w{i}", (ushort)i, Config);
                _workers.Add(worker);
                _byPeerId[PeerIdBase + i] = worker;
            }
            foreach (var a in _workers)
            {
                var byId = (System.Collections.IDictionary)a.GetField("_workerPeersById");
                var byIndex = (System.Collections.IDictionary)a.GetField("_workerPeersByIndex");
                foreach (var b in _workers)
                {
                    if (ReferenceEquals(a, b)) continue;
                    var peer = MakePeer(PeerIdBase + b.Index, b.Id, b.Index, PeerRole.Worker, b.Id);
                    a.PeersById[b.Id] = peer;
                    byId[b.Id] = peer;
                    byIndex[(uint)b.Index] = peer;
                }
            }
        }

        public Worker this[int index] => _workers[index];
        public Worker Get(string id) => _workers.Find(w => w.Id == id) ?? throw new ArgumentException($"no worker {id}");

        /// <summary>A gateway every worker hears from (see <see cref="Gateway"/>), keyed by <paramref name="id"/> and <paramref name="incarnation"/>.</summary>
        public Gateway AddGateway(string id, uint incarnation = 1)
        {
            int peerId = GatewayPeerIdBase + _gatewaysByPeerId.Count + 1;
            var gateway = new Gateway(id, PlayerSessions.GatewayKey(id, incarnation), peerId);
            _gatewaysByPeerId[peerId] = gateway;
            foreach (var w in _workers) w.PeersById[id] = MakePeer(peerId, id, 0, PeerRole.Gateway, gateway.Key);
            return gateway;
        }

        /// <summary>
        /// Deliver one message from <paramref name="gateway"/> into <paramref name="to"/>'s own <c>Dispatch</c>, as
        /// the gateway's link would. <paramref name="write"/> writes it, id byte first, as a message's own
        /// <c>Write</c> does. What the worker sends back waits in its outbox until <see cref="Pump"/>.
        /// </summary>
        public void FromGateway(Gateway gateway, Worker to, Action<NetworkWriter> write)
        {
            var w = new NetworkWriter(256);
            write(w);
            var bytes = w.ToArray();
            Delivered.Add(new WireMessage(gateway.Id, to.Id, bytes));
            to.Dispatch(to.PeersById[gateway.Id], bytes);
        }

        /// <summary>A static container every worker resolves; the mesh does not lease it to anyone.</summary>
        public Container AddStaticContainer(string id, Vector3 position, Vector3 size)
        {
            var go = new GameObject(id);
            _objects.Add(go);
            go.transform.position = position;
            var c = go.AddComponent<Container>();
            c.ContainerId = id;
            c.Size = size;
            c.Center = new Vector3(0, size.y * 0.5f, 0);
            ContainerRegistry.Rebuild();
            return c;
        }

        /// <summary>Give <paramref name="container"/> to <paramref name="owner"/>, as a control-plane lease would.</summary>
        public void SetOwner(Container container, Worker owner)
        {
            ContainerRegistry.ApplyLease(container.ContainerId, owner.Id, owner.Index, 1);
        }

        /// <summary>
        /// Register a prefab (an inactive GameObject with a <see cref="NetworkIdentity"/>) in the process-wide
        /// prefab table every worker instantiates ghosts from. Returns its prefab id.
        /// </summary>
        public ushort RegisterPrefab(GameObject prefab)
        {
            if (prefab.GetComponent<NetworkIdentity>() == null) throw new ArgumentException("a network prefab needs a NetworkIdentity");
            prefab.SetActive(false);
            _prefabs.Add(prefab);
            _objects.Add(prefab);
            NetworkPrefabs.Register(_prefabs);
            return (ushort)(_prefabs.Count - 1);
        }

        /// <summary>
        /// Deliver everything every worker has sent, in order, into the receiving worker's own <c>Dispatch</c>, and
        /// keep going until the mesh is quiet (a delivery can make the receiver send again). Returns how many
        /// messages were delivered.
        /// </summary>
        public int Pump()
        {
            int delivered = 0;
            for (int round = 0; round < MaxPumpRounds; round++)
            {
                bool moved = false;
                foreach (var from in _workers)
                {
                    while (from.Transport.Outbox.Count > 0)
                    {
                        var sent = from.Transport.Outbox.Dequeue();
                        if (_gatewaysByPeerId.TryGetValue(sent.Key, out var gateway))
                        {
                            // A gateway only listens here: what it is sent is recorded and goes no further.
                            Delivered.Add(new WireMessage(from.Id, gateway.Id, sent.Value));
                            delivered++;
                            moved = true;
                            continue;
                        }
                        if (!_byPeerId.TryGetValue(sent.Key, out var to))
                            throw new InvalidOperationException($"{from.Id} sent {(MsgId)sent.Value[0]} to unknown peer {sent.Key}");
                        var message = new WireMessage(from.Id, to.Id, sent.Value);
                        Delivered.Add(message);
                        to.Dispatch(to.PeersById[from.Id], sent.Value);
                        delivered++;
                        moved = true;
                    }
                }
                if (!moved) return delivered;
            }
            throw new InvalidOperationException($"the mesh did not settle in {MaxPumpRounds} rounds");
        }

        /// <summary>Re-deliver an already delivered message, as a duplicated or replayed packet would.</summary>
        public void Replay(WireMessage message)
        {
            var to = Get(message.To);
            to.Dispatch(to.PeersById[message.From], message.Bytes);
        }

        /// <summary>The delivered messages of one kind, optionally filtered by receiver.</summary>
        public List<WireMessage> DeliveredOf(MsgId id, string to = null)
        {
            var result = new List<WireMessage>();
            foreach (var m in Delivered) if (m.Id == id && (to == null || m.To == to)) result.Add(m);
            return result;
        }

        private static object MakePeer(int peerId, string id, uint index, PeerRole role, string key)
        {
            object peer = Activator.CreateInstance(PeerType, nonPublic: true);
            PeerType.GetField("PeerId").SetValue(peer, peerId);
            PeerType.GetField("Role").SetValue(peer, role);
            PeerType.GetField("Id").SetValue(peer, id);
            PeerType.GetField("Key").SetValue(peer, key);
            PeerType.GetField("Index").SetValue(peer, index);
            PeerType.GetField("HelloReceived").SetValue(peer, true);
            return peer;
        }

        /// <summary>Destroy every object the mesh created (workers, containers, prefabs, spawned entities) and reset the process-wide statics.</summary>
        public void Dispose()
        {
            foreach (var worker in _workers)
            {
                var entities = new List<NetworkIdentity>(worker.Instance.Entities);
                foreach (var e in entities) if (e != null) Object.DestroyImmediate(e.gameObject);
            }
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            _workers.Clear();
            if (Config != null) Object.DestroyImmediate(Config);
            NetworkPrefabs.Register(null);
            SceneEntities.Clear();
            ContainerRegistry.Rebuild();
            NebulaRuntime.Reset();
        }
    }
}
