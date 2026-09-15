using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Nebula.WebRtc;
using NUnit.Framework;
using Org.BouncyCastle.Tls;

namespace Nebula.Services.Tests;

public sealed class WebRtcTests
{
    private const uint Binary = SctpAssociation.PpidBinary;

    /// <summary>Two associations joined by a simulated link that loses packets at the given rates, stepped 10 ms at a time.</summary>
    private sealed class Link
    {
        public readonly SctpAssociation A, B;
        public readonly List<(ushort Stream, byte[] Data)> AtA = new(), AtB = new();
        public readonly List<string> Log = new();
        public int PacketsAToB, PacketsBToA;
        public double Now;
        private readonly List<byte[]> _toA = new(), _toB = new();

        public Link(double lossAToB, double lossBToA, int seed)
        {
            var random = new Random(seed);
            A = new SctpAssociation((p, n) => { PacketsAToB++; bool kept = random.NextDouble() >= lossAToB; Log.Add($"{Now:0.00} A sends {n} bytes{(kept ? "" : " (lost)")}"); if (kept) _toB.Add(p.AsSpan(0, n).ToArray()); }, (s, _, m) => AtA.Add((s, m)));
            B = new SctpAssociation((p, n) => { PacketsBToA++; bool kept = random.NextDouble() >= lossBToA; Log.Add($"{Now:0.00} B sends {n} bytes{(kept ? "" : " (lost)")}"); if (kept) _toA.Add(p.AsSpan(0, n).ToArray()); }, (s, _, m) => AtB.Add((s, m)));
            A.Trace = s => Log.Add($"{Now:0.00} A {s}");
            B.Trace = s => Log.Add($"{Now:0.00} B {s}");
        }

        public string Tail(int lines) => "\n" + string.Join("\n", Log.Skip(Math.Max(0, Log.Count - lines)));

        public void Step()
        {
            var toB = _toB.ToArray(); _toB.Clear();
            var toA = _toA.ToArray(); _toA.Clear();
            foreach (var p in toB) B.HandlePacket(p, Now);
            foreach (var p in toA) A.HandlePacket(p, Now);
            A.Tick(Now); B.Tick(Now);
            A.Flush(Now); B.Flush(Now);
            Now += 0.01;
        }

        public void Run(double seconds) { for (double end = Now + seconds; Now < end;) Step(); }

        public void Open()
        {
            A.Connect(Now);
            for (int i = 0; i < 3000 && (A.State != SctpAssociation.Status.Established || B.State != SctpAssociation.Status.Established); i++) Step();
            Assert.That(A.State, Is.EqualTo(SctpAssociation.Status.Established));
            Assert.That(B.State, Is.EqualTo(SctpAssociation.Status.Established));
        }
    }

    private static byte[] Message(int index, int size)
    {
        var m = new byte[size];
        new Random(index).NextBytes(m);
        BitConverter.GetBytes(index).CopyTo(m, 0);
        return m;
    }

    [Test]
    public void SctpDeliversReliableMessagesInOrderOverALossyLink()
    {
        var link = new Link(0.2, 0.2, 7);
        link.Open();
        var sent = Enumerable.Range(0, 300).Select(i => Message(i, i % 10 == 0 ? 3000 : 40 + i)).ToList();
        foreach (var m in sent)
        {
            Assert.That(link.A.Send(0, false, true, Binary, m));
            Assert.That(link.B.Send(0, false, true, Binary, m));
        }
        for (int i = 0; i < 12000 && (link.AtA.Count < sent.Count || link.AtB.Count < sent.Count || link.A.OutstandingCount > 0 || link.B.OutstandingCount > 0); i++) link.Step();
        string state = $"\nA: {link.A.Describe()}\nB: {link.B.Describe()}{link.Tail(60)}";
        Assert.That(link.AtB.Count, Is.EqualTo(sent.Count), "A to B" + state);
        Assert.That(link.AtB.Select(x => x.Data), Is.EqualTo(sent), "A to B" + state);
        Assert.That(link.AtA.Select(x => x.Data), Is.EqualTo(sent), "B to A" + state);
        Assert.That(link.A.OutstandingCount, Is.Zero);
        Assert.That(link.B.OutstandingCount, Is.Zero);
    }

    [Test]
    public void SctpAbandonsLostUnreliableMessagesWithoutStallingTheReliableStream()
    {
        var link = new Link(0.3, 0.05, 11);
        link.Open();
        var reliable = new List<byte[]>();
        int unreliableSent = 0;
        for (int i = 0; i < 600; i++)
        {
            link.A.Send(1, true, false, Binary, Message(100000 + i, 200));
            unreliableSent++;
            if (i % 10 == 0)
            {
                var m = Message(i, 120);
                reliable.Add(m);
                link.A.Send(0, false, true, Binary, m);
            }
            link.Step();
        }
        link.Run(30);
        Assert.That(link.AtB.Where(x => x.Stream == 0).Select(x => x.Data), Is.EqualTo(reliable));
        int unreliableReceived = link.AtB.Count(x => x.Stream == 1);
        Assert.That(unreliableReceived, Is.InRange(unreliableSent / 2, unreliableSent - 1), "some unreliable messages are lost and none are retransmitted");
        Assert.That(link.A.OutstandingCount, Is.Zero, $"abandoned chunks are skipped with FORWARD-TSN\nA: {link.A.Describe()}\nB: {link.B.Describe()}{link.Tail(60)}");

        // A quiet association stays quiet (no SACK / FORWARD-TSN ping-pong).
        int before = link.PacketsAToB + link.PacketsBToA;
        link.Run(5);
        Assert.That(link.PacketsAToB + link.PacketsBToA - before, Is.Zero);
    }

    [Test]
    public void StunBindingRequestsAreAuthenticatedAndAnswered()
    {
        var key = Encoding.UTF8.GetBytes("serverpasswordserverpassword");
        var txid = RandomNumberGenerator.GetBytes(12);
        var request = Stun.BindingRequest(txid, "srv1:cli1", key, true);
        Assert.That(Stun.TryParseBindingRequest(request, out var parsed));
        Assert.That(parsed.Username, Is.EqualTo("srv1:cli1"));
        Assert.That(parsed.UseCandidate);
        Assert.That(Stun.CheckIntegrity(request, parsed, key));
        Assert.That(Stun.CheckIntegrity(request, parsed, Encoding.UTF8.GetBytes("wrong")), Is.False);
        var response = Stun.BindingResponse(txid, new IPEndPoint(IPAddress.Parse("192.168.1.20"), 54321), key);
        Assert.That(Stun.IsBindingSuccess(response, txid));
    }

    [Test]
    public void AnswerToABrowserOfferIsIceLiteAndPassive()
    {
        const string chromeOffer = "v=0\r\no=- 4611731400430051336 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\na=group:BUNDLE 0\r\na=extmap-allow-mixed\r\na=msid-semantic: WMS\r\n" +
            "m=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\nc=IN IP4 0.0.0.0\r\na=ice-ufrag:Ab3d\r\na=ice-pwd:Q2w3e4r5t6y7u8i9o0p1a2s3\r\na=ice-options:trickle\r\n" +
            "a=fingerprint:sha-256 7B:8C:4A:22:9E:10:01:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22\r\na=setup:actpass\r\na=mid:0\r\na=sctp-port:5000\r\na=max-message-size:262144\r\n";
        Assert.That(Sdp.TryParse(chromeOffer, out var offer));
        Assert.That(offer.IceUfrag, Is.EqualTo("Ab3d"));
        Assert.That(offer.Fingerprint, Does.StartWith("7B:8C"));
        string answer = Sdp.Answer(offer, "srv12345", "passwordpasswordpassword", "AA:BB", new[] { IPAddress.Parse("10.0.0.5"), IPAddress.Loopback }, 7001);
        Assert.That(answer, Does.Contain("a=ice-lite\r\n").And.Contain("a=setup:passive\r\n").And.Contain("a=mid:0\r\n"));
        Assert.That(answer, Does.Contain("a=candidate:1 1 udp 2130706431 10.0.0.5 7001 typ host\r\n").And.Contain(" 127.0.0.1 7001 typ host\r\n"));
        Assert.That(Sdp.TryParse(answer, out var parsed) && parsed.IceUfrag == "srv12345");
    }

    /// <summary>DTLS over a plain UDP socket, skipping STUN responses (what a browser's ICE agent does for its DTLS).</summary>
    private sealed class UdpDatagrams : DatagramTransport
    {
        private readonly Socket _socket;
        private readonly EndPoint _remote;
        public UdpDatagrams(Socket socket, EndPoint remote) { _socket = socket; _remote = remote; }
        public int GetReceiveLimit() => 1500;
        public int GetSendLimit() => DatagramQueue.SendLimit;
        public int Receive(byte[] buf, int off, int len, int waitMillis)
        {
            _socket.ReceiveTimeout = Math.Max(1, waitMillis);
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                while (true)
                {
                    int n = _socket.ReceiveFrom(buf, off, len, SocketFlags.None, ref from);
                    if (n > 0 && buf[off] < 2) continue;
                    return n;
                }
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut) { return -1; }
        }
        public int Receive(Span<byte> buffer, int waitMillis)
        {
            var copy = new byte[buffer.Length];
            int n = Receive(copy, 0, copy.Length, waitMillis);
            if (n > 0) copy.AsSpan(0, n).CopyTo(buffer);
            return n;
        }
        public void Send(byte[] buf, int off, int len) => _socket.SendTo(buf, off, len, SocketFlags.None, _remote);
        public void Send(ReadOnlySpan<byte> buffer) => _socket.SendTo(buffer.ToArray(), _remote);
        public void Close() { }
    }

    [Test, Timeout(30000)]
    public void ABrowserStyleClientConnectsExchangesMessagesAndDisconnects()
    {
        using var server = new WebRtcServerTransport("test-web", "127.0.0.1");
        server.Listen(0);
        var identity = DtlsIdentity.Create();
        const string clientUfrag = "cli1", clientPwd = "clientpasswordclientpass";
        string offer = "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\na=group:BUNDLE 0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\nc=IN IP4 0.0.0.0\r\n" +
            $"a=ice-ufrag:{clientUfrag}\r\na=ice-pwd:{clientPwd}\r\na=fingerprint:sha-256 {identity.Fingerprint}\r\na=setup:actpass\r\na=mid:0\r\na=sctp-port:5000\r\n";
        Assert.That(Sdp.TryParse(server.Accept(offer, IPAddress.Loopback), out var answer));

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEndpoint = new IPEndPoint(IPAddress.Loopback, server.LocalPort);

        // ICE: one authenticated check with USE-CANDIDATE, as the controlling browser sends it.
        var txid = RandomNumberGenerator.GetBytes(12);
        socket.SendTo(Stun.BindingRequest(txid, answer.IceUfrag + ":" + clientUfrag, Encoding.UTF8.GetBytes(answer.IcePwd), true), serverEndpoint);
        var buffer = new byte[2048];
        socket.ReceiveTimeout = 3000;
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        int got = socket.ReceiveFrom(buffer, ref from);
        Assert.That(Stun.IsBindingSuccess(buffer.AsSpan(0, got), txid));

        var dtls = new DtlsClientProtocol().Connect(new DtlsClient(identity, answer.Fingerprint), new UdpDatagrams(socket, serverEndpoint));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var gate = new object();
        var atClient = new ConcurrentQueue<(ushort Stream, byte[] Data)>();
        var sctp = new SctpAssociation((p, n) => dtls.Send(p, 0, n), (s, _, m) => atClient.Enqueue((s, m)));
        bool stop = false;
        var reader = new Thread(() =>
        {
            var rb = new byte[2048];
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    int n = dtls.Receive(rb, 0, rb.Length, 50);
                    if (n > 0) lock (gate) sctp.HandlePacket(rb.AsSpan(0, n), clock.Elapsed.TotalSeconds);
                }
            }
            catch (Exception) { }
        }) { IsBackground = true };
        reader.Start();

        var events = new List<TransportEvent>();
        var data = new List<string>();
        void Pump()
        {
            lock (gate) { sctp.Tick(clock.Elapsed.TotalSeconds); sctp.Flush(clock.Elapsed.TotalSeconds); }
            server.Poll(ev =>
            {
                events.Add(new TransportEvent(ev.Type, ev.PeerId, default));
                if (ev.Type == TransportEvent.Kind.Data) data.Add(Encoding.UTF8.GetString(ev.Data.Array!, ev.Data.Offset, ev.Data.Count));
            });
            server.Flush();
            Thread.Sleep(5);
        }
        void PumpUntil(Func<bool> done, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!done())
            {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "timed out waiting for " + what);
                Pump();
            }
        }

        lock (gate) sctp.Connect(clock.Elapsed.TotalSeconds);
        PumpUntil(() => events.Any(e => e.Type == TransportEvent.Kind.Connected), "the server's Connected event");
        int peer = events.First(e => e.Type == TransportEvent.Kind.Connected).PeerId;
        Assert.That(server.IsConnected(peer));

        lock (gate) sctp.Send(0, false, true, Binary, Encoding.UTF8.GetBytes("hello"));
        PumpUntil(() => data.Contains("hello"), "the client's message");

        // Unreliable messages carry a sequence number; one older than the newest delivered is dropped.
        byte[] Framed(ushort sequence, string text) => new[] { (byte)sequence, (byte)(sequence >> 8) }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        lock (gate)
        {
            sctp.Send(WebRtcServerTransport.UnreliableStream, true, false, Binary, Framed(5, "newer"));
            sctp.Send(WebRtcServerTransport.UnreliableStream, true, false, Binary, Framed(4, "older"));
        }
        PumpUntil(() => data.Contains("newer"), "the newer unreliable message");
        for (int i = 0; i < 20; i++) Pump();
        Assert.That(data, Does.Not.Contain("older"));

        var big = Message(42, 5000);
        server.Send(peer, Delivery.ReliableOrdered, new ArraySegment<byte>(big));
        server.Send(peer, Delivery.Sequenced, new ArraySegment<byte>(Encoding.UTF8.GetBytes("state")));
        PumpUntil(() => atClient.Count >= 2, "the server's messages");
        Assert.That(atClient.Any(x => x.Stream == WebRtcServerTransport.ReliableStream && x.Data.SequenceEqual(big)));
        Assert.That(atClient.Any(x => x.Stream == WebRtcServerTransport.UnreliableStream && Encoding.UTF8.GetString(x.Data, WebRtcServerTransport.SequenceBytes, x.Data.Length - WebRtcServerTransport.SequenceBytes) == "state"));
        PumpUntil(() => server.RoundTripMs(peer) >= 0, "a round-trip measurement");

        lock (gate) sctp.Abort();
        PumpUntil(() => events.Any(e => e.Type == TransportEvent.Kind.Disconnected && e.PeerId == peer), "the server's Disconnected event");
        Assert.That(server.SessionCount, Is.Zero);
        Volatile.Write(ref stop, true);
        reader.Join(1000);
        dtls.Close();
    }

    [Test]
    public async Task TheGatewayServesTheWebBuildWithValidatorsAndAnswersBadOffers()
    {
        string root = Path.Combine(Path.GetTempPath(), "nebula-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Build"));
        File.WriteAllText(Path.Combine(root, "index.html"), "<html></html>");
        File.WriteAllBytes(Path.Combine(root, "Build", "Web.wasm.gz"), new byte[] { 1, 2, 3 });
        try
        {
            using var rtc = new WebRtcServerTransport("test-web", "127.0.0.1");
            rtc.Listen(0);
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            using var http = new GatewayHttpServer((ushort)port, rtc, root);
            http.Start();
            using var client = new HttpClient();
            string origin = $"http://127.0.0.1:{port}";

            var index = await client.GetAsync(origin + "/");
            Assert.That(index.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(index.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));

            var wasm = await client.GetAsync(origin + "/Build/Web.wasm.gz");
            Assert.That(wasm.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(wasm.Content.Headers.ContentEncoding, Does.Contain("gzip"));
            Assert.That(wasm.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/wasm"));
            Assert.That(await wasm.Content.ReadAsByteArrayAsync(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(wasm.Headers.ETag, Is.Not.Null);

            var revalidate = new HttpRequestMessage(HttpMethod.Get, origin + "/Build/Web.wasm.gz");
            revalidate.Headers.IfNoneMatch.Add(wasm.Headers.ETag!);
            Assert.That((await client.SendAsync(revalidate)).StatusCode, Is.EqualTo(HttpStatusCode.NotModified));

            Assert.That((await client.GetAsync(origin + "/Build/missing.js")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await client.PostAsync(origin + GatewayHttpServer.SignalingPath, new StringContent("not sdp"))).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public void AQueryStringReadsLikeCommandLineSwitches()
    {
        var args = new Dictionary<string, string>();
        CommandLine.ParseQuery("http://127.0.0.1:7000/index.html?nebula-name=Jo%20Doe&-nebula-connect&nebula-gateway=10.0.0.5:7000#title", args);
        Assert.That(args["nebula-name"], Is.EqualTo("Jo Doe"));
        Assert.That(args["nebula-connect"], Is.EqualTo(""));
        Assert.That(args["nebula-gateway"], Is.EqualTo("10.0.0.5:7000"));
        Assert.That(args, Has.Count.EqualTo(3));
    }

    [Test]
    public void MultiTransportKeepsPeerIdsOfEachTransportApart()
    {
        var first = new FakeTransport();
        var second = new FakeTransport();
        using var multi = new MultiTransport(first, second);
        first.Pending.Add(new TransportEvent(TransportEvent.Kind.Connected, 3, default));
        second.Pending.Add(new TransportEvent(TransportEvent.Kind.Connected, 3, default));
        var ids = new List<int>();
        multi.Poll(ev => ids.Add(ev.PeerId));
        Assert.That(ids, Has.Count.EqualTo(2));
        Assert.That(ids[0], Is.Not.EqualTo(ids[1]));
        multi.Send(ids[1], Delivery.ReliableOrdered, new ArraySegment<byte>(new byte[1]));
        Assert.That(first.SentTo, Is.Empty);
        Assert.That(second.SentTo, Is.EqualTo(new[] { 3 }));
    }

    private sealed class FakeTransport : ITransport
    {
        public readonly List<TransportEvent> Pending = new();
        public readonly List<int> SentTo = new();
        public string Name => "fake";
        public bool IsRunning => true;
        public int LocalPort => 0;
        public void Listen(int port) { }
        public void StartClient() { }
        public int Connect(string host, int port) => 0;
        public void Disconnect(int peerId) { }
        public bool IsConnected(int peerId) => true;
        public int RoundTripMs(int peerId) => -1;
        public void Send(int peerId, Delivery delivery, ArraySegment<byte> payload) => SentTo.Add(peerId);
        public void Poll(Action<TransportEvent> handler) { foreach (var e in Pending) handler(e); Pending.Clear(); }
        public void Flush() { }
        public void Stop() { }
        public void Dispose() { }
    }
}
