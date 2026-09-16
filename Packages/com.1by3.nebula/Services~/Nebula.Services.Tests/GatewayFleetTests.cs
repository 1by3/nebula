using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Nebula;
using Nebula.ServicePrimitives;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// What makes a gateway fleet safe: mesh-wide session ids, session tokens, connection generations, draining,
/// load reports and infrastructure peer authentication. The unit halves test the pieces; the fleet tests run two
/// real gateways (UDP, in-process control plane) against a fake worker.
/// </summary>
[TestFixture]
public class GatewayFleetTests
{
    private string directory = "";

    [SetUp]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "nebula-fleet-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CommandLine.Override(new Dictionary<string, string>());
        var path = Path.Combine(directory, "nebula-services.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceManifest
        {
            Containers = new() { new Container { ContainerId = "c0", Index = 0, Size = new(64, 64, 64), transform = new ContainerFrame { position = new(32, 0, 0) } } },
        }, ServiceManifest.Json));
        ServiceManifest.Load(path);
    }

    [TearDown] public void Cleanup() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    // ---------------------------------------------------------------------------------------- units

    [Test]
    public void SessionIdsCarryTheIncarnationAndNeverStartAtZero()
    {
        ulong id = SessionIds.Make(0xABCD1234, 7);
        Assert.That(SessionIds.Incarnation(id), Is.EqualTo(0xABCD1234u));
        Assert.That(SessionIds.Sequence(id), Is.EqualTo(7u));
        for (int i = 0; i < 100; i++) Assert.That(SessionIds.NewIncarnation(), Is.Not.Zero);
        Assert.That(SessionIds.NewIncarnation(), Is.Not.EqualTo(SessionIds.NewIncarnation()));
    }

    [Test]
    public void SessionTokensRoundTripAndRejectForgeries()
    {
        var key = SessionTokens.DeriveKey(AnonymousIdentityIssuer.DeriveKey("k"));
        var tokens = new SessionTokens(key);
        var claims = new SessionClaims { SessionId = SessionIds.Make(5, 9), Identity = "id-1", Name = "ann", IsBot = true, Generation = 42, ExpiresAt = JsonWebToken.UnixNow() + 60 };
        string token = tokens.Issue(claims);

        Assert.That(tokens.Verify(token, out var back, out var error), Is.True, error);
        Assert.That(back.SessionId, Is.EqualTo(claims.SessionId));
        Assert.That(back.Identity, Is.EqualTo("id-1"));
        Assert.That(back.Name, Is.EqualTo("ann"));
        Assert.That(back.IsBot, Is.True);
        Assert.That(back.Generation, Is.EqualTo(42ul));

        Assert.That(new SessionTokens(SessionTokens.DeriveKey(AnonymousIdentityIssuer.DeriveKey("other"))).Verify(token, out _, out error), Is.False);
        Assert.That(error, Does.Contain("signature"));
        Assert.That(tokens.Verify(token.Substring(0, token.Length - 2) + "AA", out _, out error), Is.False);
        Assert.That(tokens.Verify(tokens.Issue(new SessionClaims { SessionId = 1, ExpiresAt = JsonWebToken.UnixNow() - 1 }), out _, out error), Is.False);
        Assert.That(error, Does.Contain("expired"));
        Assert.That(tokens.Verify(new AnonymousIdentityIssuer(key).Issue(out _), out _, out error), Is.False, "an identity token is not a session token");
    }

    [Test]
    public void MeshPeerCredentialsBindRoleIdIncarnationAndTime()
    {
        var key = MeshPeerAuth.DeriveKey("mesh-secret");
        long now = 1_700_000_000;
        string token = MeshPeerAuth.Issue(key, PeerRole.Gateway, "gw1", 77, now);
        Assert.That(MeshPeerAuth.Verify(key, PeerRole.Gateway, "gw1", 77, token, now + 10, out var error), Is.True, error);
        Assert.That(MeshPeerAuth.Verify(key, PeerRole.Worker, "gw1", 77, token, now, out _), Is.False, "role is bound");
        Assert.That(MeshPeerAuth.Verify(key, PeerRole.Gateway, "gw2", 77, token, now, out _), Is.False, "id is bound");
        Assert.That(MeshPeerAuth.Verify(key, PeerRole.Gateway, "gw1", 78, token, now, out _), Is.False, "incarnation is bound");
        Assert.That(MeshPeerAuth.Verify(key, PeerRole.Gateway, "gw1", 77, token, now + MeshPeerAuth.MaxAgeSeconds + 1, out error), Is.False);
        Assert.That(error, Does.Contain("old"));
        Assert.That(MeshPeerAuth.Verify(MeshPeerAuth.DeriveKey("another"), PeerRole.Gateway, "gw1", 77, token, now, out _), Is.False);
        Assert.That(MeshPeerAuth.Verify(key, PeerRole.Gateway, "gw1", 77, "", now, out _), Is.False);
        Assert.That(MeshPeerAuth.Verify("", PeerRole.Gateway, "gw1", 77, "", out _), Is.True, "no mesh token: open, as before");
        Assert.That(MeshPeerAuth.Issue("", PeerRole.Gateway, "gw1", 77), Is.Empty);
    }

    [Test]
    public void PlayerSessionsFenceStaleGatewaysAndExpireOrphans()
    {
        var s = new PlayerSessions();
        string a = PlayerSessions.GatewayKey("gw1", 1), b = PlayerSessions.GatewayKey("gw2", 1);
        Assert.That(s.Register(10, 100, a), Is.EqualTo(PlayerSessions.Claim.New));
        Assert.That(s.Register(10, 100, a), Is.EqualTo(PlayerSessions.Claim.Repeat));
        Assert.That(s.Accept(10, a), Is.True);
        Assert.That(s.Accept(10, b), Is.False, "another gateway may not drive the session");

        Assert.That(s.Register(10, 200, b), Is.EqualTo(PlayerSessions.Claim.Reclaimed));
        Assert.That(s.Accept(10, b), Is.True);
        Assert.That(s.Accept(10, a), Is.False, "the old gateway is fenced");
        Assert.That(s.Register(10, 150, a), Is.EqualTo(PlayerSessions.Claim.Stale));
        Assert.That(s.Release(10, 100, now: 5), Is.False, "a despawn from the old gateway is stale");
        Assert.That(s.OrphanCount, Is.Zero);

        Assert.That(s.Release(10, 200, now: 5), Is.True);
        Assert.That(s.OrphanCount, Is.EqualTo(1));
        var expired = new List<ulong>();
        s.Expire(now: 6, graceSeconds: 30, expired);
        Assert.That(expired, Is.Empty, "within the grace nothing expires");
        Assert.That(s.Register(10, 300, a), Is.EqualTo(PlayerSessions.Claim.Reclaimed), "a reclaim within the grace rescues the session");
        Assert.That(s.OrphanCount, Is.Zero);

        s.GatewayLost(a, now: 10);
        Assert.That(s.OrphanCount, Is.EqualTo(1));
        s.GatewayReturned(a);
        Assert.That(s.OrphanCount, Is.Zero, "the same incarnation coming back rescues its sessions");
        s.GatewayLost(a, now: 20);
        s.Expire(now: 51, graceSeconds: 30, expired);
        Assert.That(expired, Is.EqualTo(new[] { 10ul }));
        Assert.That(s.Count, Is.Zero);

        Assert.That(s.Accept(11, b), Is.True, "an unknown session is learnt lazily (a handover brought the pawn)");
        s.Adopt(12, 5, a);
        s.Adopt(12, 4, b);
        Assert.That(s.TryGet(12, out var adopted) && adopted.Gateway == a, Is.True, "adopt never lowers a generation");
        Assert.That(s.Release(13, 1, now: 0), Is.True, "an unknown session's despawn is current");
        Assert.That(s.OrphanCount, Is.EqualTo(1));
    }

    [Test]
    public void SpawnAndDespawnCarryTheGenerationOnTheWire()
    {
        var w = new NetworkWriter();
        new SpawnPlayerMsg { ClientId = SessionIds.Make(3, 4), Container = new ContainerRef(0), Name = "n", Identity = "i", Generation = 1234567890123 }.Write(w);
        var r = new NetworkReader(w.ToArray()); r.ReadByte();
        var spawn = SpawnPlayerMsg.Read(r);
        Assert.That(spawn.ClientId, Is.EqualTo(SessionIds.Make(3, 4)));
        Assert.That(spawn.Generation, Is.EqualTo(1234567890123ul));
        w.Reset();
        new DespawnPlayerMsg { ClientId = 9, Generation = 8 }.Write(w);
        r = new NetworkReader(w.ToArray()); r.ReadByte();
        var despawn = DespawnPlayerMsg.Read(r);
        Assert.That((despawn.ClientId, despawn.Generation), Is.EqualTo((9ul, 8ul)));
        w.Reset();
        new HelloMsg { Role = PeerRole.Client, Id = "x", Session = "tok", Incarnation = 5, Token = "t" }.Write(w);
        r = new NetworkReader(w.ToArray()); r.ReadByte();
        var hello = HelloMsg.Read(r);
        Assert.That((hello.Session, hello.Incarnation, hello.Token), Is.EqualTo(("tok", 5u, "t")));
    }

    [Test]
    public void GatewayStatsTravelThroughTheControlPlaneJson()
    {
        using var cp = new LocalControlPlane(); cp.Connect();
        cp.RegisterGateway("gw1", "203.0.113.9", 7000, 0xdeadbeef);
        cp.HeartbeatGateway("gw1", new GatewayStats { PendingJoins = 1, ActiveClients = 2, ReconnectingClients = 3, BytesOutPerSecond = 4.5f, Cpu = 0.5f, LoopLagMs = 6, WorkerConnections = 7, Ready = true, Draining = true, MemoryBytes = 8 });
        cp.SetGatewayDraining("gw1", true);
        var parsed = ControlPlaneJson.Parse(cp.ToJson());
        var g = parsed.Gateways[0];
        Assert.That(g.Incarnation, Is.EqualTo(0xdeadbeefu));
        Assert.That(g.DrainRequested, Is.True);
        Assert.That((g.Stats.PendingJoins, g.Stats.ActiveClients, g.Stats.ReconnectingClients, g.Stats.WorkerConnections), Is.EqualTo((1u, 2u, 3u, 7u)));
        Assert.That(g.Stats.BytesOutPerSecond, Is.EqualTo(4.5f).Within(0.01));
        Assert.That(g.Stats.LoopLagMs, Is.EqualTo(6f).Within(0.01));
        Assert.That(g.Stats.MemoryBytes, Is.EqualTo(8ul));
        Assert.That(g.Stats.Ready && g.Stats.Draining, Is.True);

        using var applied = new LocalControlPlane(); applied.Connect();
        Assert.That(ControlPlaneJson.ApplyBatch("{\"ops\":[{\"op\":\"RegisterGateway\",\"gatewayId\":\"gw2\",\"address\":\"a\",\"port\":1,\"incarnation\":9},{\"op\":\"SetGatewayDraining\",\"gatewayId\":\"gw2\",\"draining\":true},{\"op\":\"HeartbeatGateway\",\"gatewayId\":\"gw2\",\"activeClients\":5,\"draining\":true}]}", applied), Is.Null);
        Assert.That(applied.Gateways[0].Incarnation, Is.EqualTo(9u));
        Assert.That(applied.Gateways[0].DrainRequested, Is.True);
        Assert.That(applied.Gateways[0].Stats.ActiveClients, Is.EqualTo(5u));
        applied.RegisterGateway("gw2", "a", 1, 10);
        Assert.That(applied.Gateways[0].DrainRequested, Is.False, "a new incarnation clears a stale drain request");
    }

    // ---------------------------------------------------------------------------------------- fleet

    /// <summary>A worker as the gateway sees it: answers Hello, spawns a pawn per claim, and records what it was told.</summary>
    private sealed class FakeWorker : IDisposable
    {
        public readonly LiteNetTransport Transport = new LiteNetTransport("fake-worker");
        public readonly int Port;
        public readonly List<SpawnPlayerMsg> Claims = new();
        public readonly List<DespawnPlayerMsg> Despawns = new();
        public readonly Dictionary<int, string> Gateways = new();
        public readonly List<string> Refused = new();
        public readonly List<ClientInputMsg> Inputs = new();
        private readonly Dictionary<ulong, ulong> _pawns = new();
        private readonly string _meshToken;
        private ulong _nextNetId = 1000;
        private readonly NetworkWriter _w = new NetworkWriter();

        public FakeWorker(string meshToken)
        {
            _meshToken = meshToken;
            Transport.Listen(0);
            Port = Transport.LocalPort;
        }

        public void Poll()
        {
            Transport.Poll(e =>
            {
                if (e.Type != TransportEvent.Kind.Data) return;
                var r = new NetworkReader(e.Data);
                var id = (MsgId)r.ReadByte();
                switch (id)
                {
                    case MsgId.Hello:
                    {
                        var hello = HelloMsg.Read(r);
                        if (!MeshPeerAuth.Verify(_meshToken, hello.Role, hello.Id, hello.Incarnation, hello.Token, out string error)) { Refused.Add(hello.Id + ": " + error); Transport.Disconnect(e.PeerId); return; }
                        Gateways[e.PeerId] = hello.Id;
                        _w.Reset();
                        new HelloMsg { Role = PeerRole.Worker, Id = "w1", Index = 1, Incarnation = 1, Token = MeshPeerAuth.Issue(_meshToken, PeerRole.Worker, "w1", 1) }.Write(_w);
                        Transport.Send(e.PeerId, Delivery.ReliableOrdered, _w.ToSegment());
                        // Announce what we hold, as a real worker does on a gateway's Hello.
                        foreach (var kv in _pawns) SendSpawn(e.PeerId, kv.Value, kv.Key);
                        break;
                    }
                    case MsgId.SpawnPlayer:
                    {
                        var msg = SpawnPlayerMsg.Read(r);
                        Claims.Add(msg);
                        if (!_pawns.TryGetValue(msg.ClientId, out ulong netId)) { netId = _nextNetId++; _pawns[msg.ClientId] = netId; }
                        SendSpawn(e.PeerId, netId, msg.ClientId);
                        break;
                    }
                    case MsgId.DespawnPlayer: Despawns.Add(DespawnPlayerMsg.Read(r)); break;
                    case MsgId.ClientInput: Inputs.Add(ClientInputMsg.Read(r)); break;
                }
            });
            Transport.Flush();
        }

        private void SendSpawn(int peer, ulong netId, ulong owner)
        {
            _w.Reset();
            new EntitySpawnMsg { NetId = netId, OwnerClientId = owner, Epoch = 1, Container = new ContainerRef(0), LocalRotation = Quaternion.identity, LocalScale = Vector3.one }.Write(_w, MsgId.EntitySpawn);
            Transport.Send(peer, Delivery.ReliableOrdered, _w.ToSegment());
        }

        public void Dispose() => Transport.Dispose();
    }

    /// <summary>A client link: sends Hello on connect and collects what the gateway says (unpacking batches).</summary>
    private sealed class FakeClient : IDisposable
    {
        public readonly LiteNetTransport Transport = new LiteNetTransport("fake-client");
        public WelcomeMsg? Welcome;
        public JoinRejectedMsg? Rejected;
        public JoinState Join;
        public int DrainWithin = -1;
        public bool Disconnected;
        public readonly List<ulong> Spawned = new();
        private readonly string _token, _session, _name;
        private int _peer = -1;

        public FakeClient(int port, string name, string token = "", string session = "")
        {
            _name = name; _token = token; _session = session;
            _peer = Transport.Connect("127.0.0.1", port);
        }

        public void SendInput()
        {
            var w = new NetworkWriter();
            new ClientInputMsg { Frames = new List<ClientInputMsg.Frame> { new() { Tick = 1, Payload = new byte[] { 1 } } } }.Write(w, MsgId.ClientInput);
            Transport.Send(_peer, Delivery.Sequenced, w.ToSegment());
        }

        public void Poll()
        {
            Transport.Poll(e =>
            {
                if (e.Type == TransportEvent.Kind.Connected)
                {
                    var w = new NetworkWriter();
                    new HelloMsg { Role = PeerRole.Client, Id = _name, Token = _token, Session = _session }.Write(w);
                    Transport.Send(e.PeerId, Delivery.ReliableOrdered, w.ToSegment());
                }
                else if (e.Type == TransportEvent.Kind.Disconnected) Disconnected = true;
                else if (e.Type == TransportEvent.Kind.Data) Dispatch(new NetworkReader(e.Data));
            });
            Transport.Flush();
        }

        private void Dispatch(NetworkReader r)
        {
            var id = (MsgId)r.ReadByte();
            switch (id)
            {
                case MsgId.Batch:
                {
                    int n = r.ReadUShort();
                    for (int i = 0; i < n; i++) Dispatch(new NetworkReader(r.ReadSegment(r.ReadUShort())));
                    break;
                }
                case MsgId.Welcome: Welcome = WelcomeMsg.Read(r); break;
                case MsgId.JoinRejected: Rejected = JoinRejectedMsg.Read(r); break;
                case MsgId.JoinStatus: Join = JoinStatusMsg.Read(r).State; break;
                case MsgId.GatewayDraining: DrainWithin = GatewayDrainingMsg.Read(r).ReconnectWithinSeconds; break;
                case MsgId.EntitySpawn: Spawned.Add(EntitySpawnMsg.Read(r).NetId); break;
            }
        }

        public void Disconnect() { Transport.Disconnect(_peer); }
        public void Dispose() => Transport.Dispose();
    }

    private sealed class Fleet : IDisposable
    {
        public readonly LocalControlPlane Plane = new LocalControlPlane();
        public readonly FakeWorker Worker;
        public readonly List<NebulaGateway> Gateways = new();
        public readonly List<int> Ports = new();
        public readonly List<FakeClient> Clients = new();
        private readonly string _meshToken;

        public Fleet(int gateways, string meshToken = "", float reclaimSeconds = 30f)
        {
            _meshToken = meshToken;
            Plane.Connect();
            Worker = new FakeWorker(meshToken);
            Plane.RegisterWorker("w1", 1, "127.0.0.1", (ushort)Worker.Port);
            Plane.HeartbeatWorker("w1", WorkerStatus.Ready, new WorkerStats());
            Plane.EnsureContainer("c0");
            Plane.AssignContainer("c0", "w1");
            for (int i = 0; i < gateways; i++)
            {
                using var reserve = new UdpClient(0);
                int port = ((IPEndPoint)reserve.Client.LocalEndPoint!).Port; reserve.Close();
                var gw = new NebulaGateway();
                gw.Initialize(new NebulaConfig { GatewayPort = (ushort)port, AuthSigningKey = "fleet-key", MeshToken = meshToken, WebClients = false, SessionReclaimSeconds = reclaimSeconds, GatewayDrainReconnectSeconds = 7 }, Plane, null, "gw" + (i + 1));
                Gateways.Add(gw);
                Ports.Add(port);
            }
        }

        public FakeClient Connect(int gateway, string name, string token = "", string session = "")
        {
            var c = new FakeClient(Ports[gateway], name, token, session);
            Clients.Add(c);
            return c;
        }

        /// <summary>Pump everything until <paramref name="until"/> holds or the time is up; returns whether it held.</summary>
        public bool Run(Func<bool> until, double seconds = 5)
        {
            var clock = Stopwatch.StartNew();
            bool ok = false;
            while (clock.Elapsed.TotalSeconds < seconds && !(ok = until()))
            {
                Plane.HeartbeatWorker("w1", WorkerStatus.Ready, new WorkerStats());
                Plane.Tick();
                foreach (var g in Gateways) g.Tick();
                Worker.Poll();
                foreach (var c in Clients) c.Poll();
                Thread.Sleep(5);
            }
            return ok;
        }

        public void Dispose()
        {
            foreach (var c in Clients) c.Dispose();
            foreach (var g in Gateways) g.Dispose();
            Worker.Dispose();
            Plane.Dispose();
        }
    }

    [Test]
    public void TwoGatewaysIssueDistinctSessionIdsAndBothReachTheWorker()
    {
        using var fleet = new Fleet(2);
        Assert.That(fleet.Run(() => fleet.Worker.Gateways.Count == 2), Is.True, "both gateways dial the worker");
        Assert.That(fleet.Gateways[0].Incarnation, Is.Not.EqualTo(fleet.Gateways[1].Incarnation));

        var a = fleet.Connect(0, "ann");
        var b = fleet.Connect(1, "bob");
        Assert.That(fleet.Run(() => a.Join == JoinState.Joined && b.Join == JoinState.Joined), Is.True, "both clients get a pawn");
        Assert.That(a.Welcome!.Value.ClientId, Is.Not.EqualTo(b.Welcome!.Value.ClientId));
        Assert.That(SessionIds.Incarnation(a.Welcome.Value.ClientId), Is.EqualTo(fleet.Gateways[0].Incarnation));
        Assert.That(SessionIds.Incarnation(b.Welcome.Value.ClientId), Is.EqualTo(fleet.Gateways[1].Incarnation));
        Assert.That(a.Welcome.Value.SessionToken, Is.Not.Empty);
        Assert.That(a.Welcome.Value.Reclaimed, Is.False);
        Assert.That(fleet.Worker.Claims.Select(c => c.ClientId), Is.EquivalentTo(new[] { a.Welcome.Value.ClientId, b.Welcome.Value.ClientId }));
        Assert.That(fleet.Worker.Claims.All(c => c.Generation > 0), Is.True);

        // Load reports: each gateway sees its one client and the one worker.
        Assert.That(fleet.Run(() => fleet.Plane.Gateways.Count == 2 && fleet.Plane.Gateways.All(g => g.Stats.ActiveClients == 1 && g.Stats.WorkerConnections == 1 && g.Stats.Ready)), Is.True);
        Assert.That(fleet.Plane.Gateways.Select(g => g.Incarnation), Is.EquivalentTo(fleet.Gateways.Select(g => g.Incarnation)));
        a.SendInput();
        Assert.That(fleet.Run(() => fleet.Worker.Inputs.Count > 0), Is.True);
        Assert.That(fleet.Worker.Inputs[0].ClientId, Is.EqualTo(a.Welcome.Value.ClientId), "the gateway stamps the session id on inputs");
    }

    [Test]
    public void ASessionTokenReclaimsTheSameSessionOnAnotherGateway()
    {
        using var fleet = new Fleet(2);
        var first = fleet.Connect(0, "ann");
        Assert.That(fleet.Run(() => first.Join == JoinState.Joined), Is.True);
        var welcome = first.Welcome!.Value;
        ulong generation = fleet.Worker.Claims[0].Generation;

        first.Disconnect();
        Assert.That(fleet.Run(() => fleet.Worker.Despawns.Count == 1), Is.True, "the losing gateway releases the session");
        Assert.That(fleet.Worker.Despawns[0].ClientId, Is.EqualTo(welcome.ClientId));
        Assert.That(fleet.Worker.Despawns[0].Generation, Is.EqualTo(generation));

        var second = fleet.Connect(1, "ann", welcome.Token, welcome.SessionToken);
        Assert.That(fleet.Run(() => second.Join == JoinState.Joined), Is.True, "reclaimed through the other gateway");
        Assert.That(second.Welcome!.Value.ClientId, Is.EqualTo(welcome.ClientId), "same session id");
        Assert.That(second.Welcome.Value.Identity, Is.EqualTo(welcome.Identity));
        Assert.That(second.Welcome.Value.Reclaimed, Is.True);
        Assert.That(second.Welcome.Value.SessionToken, Is.Not.EqualTo(welcome.SessionToken), "a fresh token for the new generation");
        Assert.That(fleet.Worker.Claims.Count, Is.EqualTo(2));
        Assert.That(fleet.Worker.Claims[1].ClientId, Is.EqualTo(welcome.ClientId));
        Assert.That(fleet.Worker.Claims[1].Generation, Is.GreaterThan(generation), "the reclaim outranks the old gateway");
        Assert.That(second.Spawned, Does.Contain(1000ul), "the worker re-announced the same pawn, not a new one");

        // A token for another identity does not reclaim the session.
        var stranger = fleet.Connect(0, "eve", "", welcome.SessionToken);
        Assert.That(fleet.Run(() => stranger.Join == JoinState.Joined), Is.True);
        Assert.That(stranger.Welcome!.Value.ClientId, Is.Not.EqualTo(welcome.ClientId));
        Assert.That(stranger.Welcome.Value.Reclaimed, Is.False);
    }

    [Test]
    public void ADrainRequestTellsClientsToReconnectAndRefusesNewOnes()
    {
        using var fleet = new Fleet(2);
        var a = fleet.Connect(0, "ann");
        Assert.That(fleet.Run(() => a.Join == JoinState.Joined), Is.True);

        fleet.Plane.SetGatewayDraining("gw1", true);
        Assert.That(fleet.Run(() => a.DrainWithin == 7), Is.True, "connected clients are told to reconnect within GatewayDrainReconnectSeconds");
        Assert.That(fleet.Gateways[0].Draining, Is.True);
        Assert.That(fleet.Gateways[0].IsReady, Is.False);
        Assert.That(fleet.Gateways[1].IsReady, Is.True);

        var late = fleet.Connect(0, "late");
        Assert.That(fleet.Run(() => late.Rejected != null), Is.True);
        Assert.That(late.Rejected!.Value.Retry, Is.True, "the refusal is about the gateway, so the client retries elsewhere");
        Assert.That(late.Rejected.Value.Reason, Does.Contain("draining"));
        Assert.That(fleet.Run(() => fleet.Plane.FindGateway("gw1")!.Stats.Draining), Is.True, "the heartbeat acknowledges the drain");

        // The client moves to gw2 with its token; the draining gateway's later despawn is fenced by the generation.
        var moved = fleet.Connect(1, "ann", a.Welcome!.Value.Token, a.Welcome.Value.SessionToken);
        Assert.That(fleet.Run(() => moved.Join == JoinState.Joined), Is.True);
        a.Disconnect();
        Assert.That(fleet.Run(() => fleet.Worker.Despawns.Count == 1), Is.True);
        Assert.That(fleet.Worker.Despawns[0].Generation, Is.LessThan(fleet.Worker.Claims.Last().Generation));

        fleet.Plane.SetGatewayDraining("gw1", false);
        Assert.That(fleet.Run(() => !fleet.Gateways[0].Draining), Is.True, "a drain can be cancelled");
    }

    [Test]
    public void PeersWithoutTheMeshTokenAreRefused()
    {
        using var honest = new Fleet(1, "secret");
        Assert.That(honest.Run(() => honest.Worker.Gateways.Count == 1), Is.True, "a gateway with the mesh token is accepted by the worker");
        Assert.That(honest.Run(() => honest.Gateways[0].WorkerCount == 1), Is.True, "and the worker's own credential is accepted by the gateway");
        Assert.That(honest.Worker.Refused, Is.Empty);

        // An impostor that dials the worker as a gateway without the credential.
        using var impostor = new LiteNetTransport("impostor");
        int peer = impostor.Connect("127.0.0.1", honest.Worker.Port);
        bool refused = false;
        honest.Run(() =>
        {
            impostor.Poll(e =>
            {
                if (e.Type == TransportEvent.Kind.Connected)
                {
                    var w = new NetworkWriter();
                    new HelloMsg { Role = PeerRole.Gateway, Id = "gw9", Incarnation = 9 }.Write(w);
                    impostor.Send(peer, Delivery.ReliableOrdered, w.ToSegment());
                }
                if (e.Type == TransportEvent.Kind.Disconnected) refused = true;
            });
            return refused;
        }, 5);
        Assert.That(refused, Is.True);
        Assert.That(honest.Worker.Refused, Has.Count.EqualTo(1));
        Assert.That(honest.Worker.Refused[0], Does.Contain("no mesh credential"));
    }
}
