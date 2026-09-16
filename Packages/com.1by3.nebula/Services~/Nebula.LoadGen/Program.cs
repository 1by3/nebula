using System.Diagnostics;
using System.Globalization;
using System.Text;
using Nebula;

namespace Nebula.LoadGen;

/// <summary>
/// Synthetic clients for a gateway fleet. Each client connects to the gateway address (a load balancer or one
/// gateway), completes the Hello/Welcome handshake, sends inputs at the tick rate, consumes replication, and
/// reconnects with its session token when the gateway tells it to (<see cref="MsgId.GatewayDraining"/>), when the
/// gateway refuses it with a retry (<see cref="JoinRejectedMsg.Retry"/>), or when the link drops. Once a second it
/// prints totals: connected, joined, reconnects, session ids that changed across a reconnect, pawns lost across a
/// reconnect, packets and bytes in and out, RTT percentiles, and which gateway each client is on (from the
/// incarnation in its session id). Exit code 0 when no session id changed and no pawn was lost.
/// <code>
/// nebula-loadgen --gateway 203.0.113.10:7000 --clients 300 --seconds 600 [--ramp 30] [--input-hz 60] [--name-prefix load]
///                [--bot] [--token &lt;identity token&gt;] [--reconnect-every 120] [--csv out.csv]
/// </code>
/// </summary>
public static class Program
{
    private sealed class Options
    {
        public string Host = "127.0.0.1";
        public int Port = 7000;
        public int Clients = 10;
        public double Seconds = 60;
        public double RampSeconds = 5;
        public int InputHz = 60;
        public string NamePrefix = "load";
        public bool Bot;
        public string Token = "";
        public double ReconnectEvery;
        public string? Csv;
        public bool Verbose;
    }

    private sealed class Client
    {
        public readonly int Index;
        public readonly LiteNetTransport Transport;
        public int Peer = -1;
        public string Name;
        public WelcomeMsg? Welcome;
        public string SessionToken = "";
        public string IdentityToken;
        public ulong SessionId;
        public ulong Pawn;
        public JoinState Join;
        public bool Connected;
        public double ConnectedAt, NextInput, NextPing, LastPongAt;
        public uint Tick;
        public int Reconnects, SessionChanges, PawnLosses, Rejections;
        public double ReconnectAt;
        public string LastError = "";
        public readonly List<double> Rtts = new();
        public double NextReconnectDrill;

        public Client(int index, string name, string identityToken)
        {
            Index = index; Name = name; IdentityToken = identityToken;
            Transport = new LiteNetTransport("loadgen-" + index);
        }
    }

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double Now => Clock.Elapsed.TotalSeconds;
    private static long _packetsIn, _packetsOut, _bytesIn, _bytesOut;

    public static int Main(string[] args)
    {
        var o = Parse(args);
        if (o == null) return 2;
        Console.WriteLine($"nebula-loadgen: {o.Clients} clients -> {o.Host}:{o.Port} for {o.Seconds:0} s (ramp {o.RampSeconds:0} s, inputs {o.InputHz} Hz{(o.ReconnectEvery > 0 ? $", reconnect drill every {o.ReconnectEvery:0} s" : "")})");
        var clients = new List<Client>();
        for (int i = 0; i < o.Clients; i++) clients.Add(new Client(i, $"{o.NamePrefix}{i}", o.Token));
        StreamWriter? csv = o.Csv != null ? new StreamWriter(o.Csv) : null;
        csv?.WriteLine("t,connected,joined,reconnects,sessionChanges,pawnLosses,rejections,packetsIn,packetsOut,bytesIn,bytesOut,rttP50,rttP95,gateways");

        double start = Now, nextReport = start + 1, end = start + o.Seconds;
        var writer = new NetworkWriter(512);
        var reader = new NetworkReader();
        while (Now < end)
        {
            double now = Now;
            // Ramp: connect clients spread over the ramp window; reconnect the ones that are due.
            for (int i = 0; i < clients.Count; i++)
            {
                var c = clients[i];
                double due = start + (o.RampSeconds * i / Math.Max(1, clients.Count));
                if (c.Peer < 0 && now >= Math.Max(due, c.ReconnectAt)) Connect(c, o);
                if (o.ReconnectEvery > 0 && c.Join == JoinState.Joined && c.NextReconnectDrill > 0 && now >= c.NextReconnectDrill)
                {
                    // A deliberate drop: the client should get the same session and pawn back.
                    c.Reconnects++;
                    Drop(c, "drill", 0.2);
                    c.NextReconnectDrill = now + o.ReconnectEvery;
                }
            }
            foreach (var c in clients)
            {
                c.Transport.Poll(e => HandleEvent(c, e, writer, reader, o));
                if (c.Connected && c.Welcome != null)
                {
                    if (now >= c.NextInput)
                    {
                        c.NextInput = now + 1.0 / o.InputHz;
                        writer.Reset();
                        new ClientInputMsg { ClientId = c.SessionId, Frames = new List<ClientInputMsg.Frame> { new() { Tick = ++c.Tick, Payload = InputPayload(c) } } }.Write(writer, MsgId.ClientInput);
                        Send(c, Delivery.Sequenced, writer.ToSegment());
                    }
                    if (now >= c.NextPing)
                    {
                        c.NextPing = now + 0.5;
                        writer.Reset();
                        new PingMsg { ClientTime = now }.Write(writer);
                        Send(c, Delivery.Sequenced, writer.ToSegment());
                    }
                }
                c.Transport.Flush();
            }
            if (now >= nextReport)
            {
                nextReport += 1;
                Report(clients, now - start, csv, o);
            }
            Thread.Sleep(1);
        }
        Report(clients, Now - start, csv, o);
        foreach (var c in clients) { try { if (c.Peer >= 0) c.Transport.Disconnect(c.Peer); c.Transport.Dispose(); } catch { } }
        csv?.Dispose();
        int sessionChanges = clients.Sum(c => c.SessionChanges), pawnLosses = clients.Sum(c => c.PawnLosses);
        Console.WriteLine($"done: {clients.Sum(c => c.Reconnects)} reconnects, {sessionChanges} session id changes, {pawnLosses} pawn losses, {clients.Sum(c => c.Rejections)} rejections");
        return sessionChanges == 0 && pawnLosses == 0 ? 0 : 1;
    }

    private static void Connect(Client c, Options o)
    {
        c.Peer = c.Transport.Connect(o.Host, o.Port);
        c.Connected = false;
        c.ConnectedAt = Now;
        c.ReconnectAt = Now + 5; // if nothing happens, try again
    }

    private static void Drop(Client c, string why, double retryIn)
    {
        if (c.Peer >= 0) { try { c.Transport.Disconnect(c.Peer); } catch { } }
        c.Peer = -1;
        c.Connected = false;
        c.Join = JoinState.None;
        c.ReconnectAt = Now + retryIn;
        c.LastError = why;
    }

    private static void HandleEvent(Client c, TransportEvent e, NetworkWriter writer, NetworkReader reader, Options o)
    {
        switch (e.Type)
        {
            case TransportEvent.Kind.Connected:
                c.Connected = true;
                writer.Reset();
                new HelloMsg { Role = PeerRole.Client, Id = c.Name, Flags = o.Bot ? HelloFlags.Bot : HelloFlags.None, Token = c.IdentityToken, Session = c.SessionToken }.Write(writer);
                Send(c, Delivery.ReliableOrdered, writer.ToSegment());
                break;
            case TransportEvent.Kind.Disconnected:
                if (c.Peer >= 0)
                {
                    c.Reconnects++;
                    Drop(c, "link dropped", 1.0);
                }
                break;
            case TransportEvent.Kind.Data:
                Interlocked.Increment(ref _packetsIn);
                Interlocked.Add(ref _bytesIn, e.Data.Count);
                reader.Set(e.Data);
                try { Dispatch(c, reader, o); } catch (Exception ex) { c.LastError = "bad packet: " + ex.Message; }
                break;
        }
    }

    private static void Dispatch(Client c, NetworkReader r, Options o)
    {
        var id = (MsgId)r.ReadByte();
        switch (id)
        {
            case MsgId.Batch:
            {
                int n = r.ReadUShort();
                for (int i = 0; i < n; i++) Dispatch(c, new NetworkReader(r.ReadSegment(r.ReadUShort())), o);
                break;
            }
            case MsgId.Welcome:
            {
                var w = WelcomeMsg.Read(r);
                bool reconnect = c.Welcome != null;
                if (reconnect && w.ClientId != c.SessionId) c.SessionChanges++;
                if (reconnect && !w.Reclaimed && c.Pawn != 0) c.PawnLosses++;
                c.Welcome = w;
                c.SessionId = w.ClientId;
                if (!string.IsNullOrEmpty(w.SessionToken)) c.SessionToken = w.SessionToken;
                if (!string.IsNullOrEmpty(w.Token)) c.IdentityToken = w.Token;
                if (!w.Reclaimed) c.Pawn = 0;
                c.ReconnectAt = 0;
                if (o.ReconnectEvery > 0 && c.NextReconnectDrill == 0) c.NextReconnectDrill = Now + o.ReconnectEvery * (0.5 + c.Index % 100 / 100.0);
                if (o.Verbose) Console.WriteLine($"{c.Name}: welcome session {w.ClientId:x16} gateway {SessionIds.Incarnation(w.ClientId):x8}{(w.Reclaimed ? " (reclaimed)" : "")}");
                break;
            }
            case MsgId.JoinRejected:
            {
                var rejected = JoinRejectedMsg.Read(r);
                c.Rejections++;
                c.LastError = "rejected: " + rejected.Reason;
                if (o.Verbose) Console.WriteLine($"{c.Name}: {c.LastError}{(rejected.Retry ? " (retry)" : "")}");
                Drop(c, c.LastError, rejected.Retry ? 1.0 : 30.0);
                break;
            }
            case MsgId.JoinStatus: c.Join = JoinStatusMsg.Read(r).State; break;
            case MsgId.GatewayDraining:
            {
                var d = GatewayDrainingMsg.Read(r);
                if (o.Verbose) Console.WriteLine($"{c.Name}: gateway draining, reconnecting ({d.ReconnectWithinSeconds} s)");
                c.Reconnects++;
                Drop(c, "draining", 0.1 + c.Index % 10 * 0.05);
                break;
            }
            case MsgId.EntitySpawn:
            {
                var spawn = EntitySpawnMsg.Read(r);
                if (spawn.OwnerClientId == c.SessionId && c.SessionId != 0) c.Pawn = spawn.NetId;
                break;
            }
            case MsgId.EntityDespawn:
            {
                var despawn = EntityDespawnMsg.Read(r);
                if (despawn.NetId == c.Pawn) c.Pawn = 0;
                break;
            }
            case MsgId.Pong:
            {
                var pong = PongMsg.Read(r);
                double rtt = (Now - pong.ClientTime) * 1000;
                c.LastPongAt = Now;
                if (c.Rtts.Count < 200) c.Rtts.Add(rtt); else c.Rtts[c.Index % 200] = rtt;
                break;
            }
        }
    }

    private static void Send(Client c, Delivery delivery, ArraySegment<byte> payload)
    {
        if (c.Peer < 0) return;
        Interlocked.Increment(ref _packetsOut);
        Interlocked.Add(ref _bytesOut, payload.Count);
        c.Transport.Send(c.Peer, delivery, payload);
    }

    private static readonly byte[] Payload = new byte[16];
    private static byte[] InputPayload(Client c)
    {
        // The game decodes inputs; a gateway forwards bytes. Vary them so nothing upstream can dedupe.
        Payload[0] = (byte)(c.Tick & 0xff);
        Payload[1] = (byte)(c.Index & 0xff);
        return Payload;
    }

    private static long _lastPacketsIn, _lastPacketsOut, _lastBytesIn, _lastBytesOut;

    private static void Report(List<Client> clients, double t, StreamWriter? csv, Options o)
    {
        int connected = clients.Count(c => c.Connected), joined = clients.Count(c => c.Join == JoinState.Joined);
        int reconnects = clients.Sum(c => c.Reconnects), changes = clients.Sum(c => c.SessionChanges), losses = clients.Sum(c => c.PawnLosses), rejections = clients.Sum(c => c.Rejections);
        long pin = Interlocked.Read(ref _packetsIn), pout = Interlocked.Read(ref _packetsOut), bin = Interlocked.Read(ref _bytesIn), bout = Interlocked.Read(ref _bytesOut);
        long dpin = pin - _lastPacketsIn, dpout = pout - _lastPacketsOut, dbin = bin - _lastBytesIn, dbout = bout - _lastBytesOut;
        _lastPacketsIn = pin; _lastPacketsOut = pout; _lastBytesIn = bin; _lastBytesOut = bout;
        var rtts = clients.SelectMany(c => c.Rtts).OrderBy(x => x).ToList();
        double p50 = rtts.Count > 0 ? rtts[rtts.Count / 2] : 0, p95 = rtts.Count > 0 ? rtts[(int)(rtts.Count * 0.95)] : 0;
        var gateways = clients.Where(c => c.SessionId != 0).GroupBy(c => SessionIds.Incarnation(c.SessionId)).Select(g => $"{g.Key:x8}={g.Count()}").ToList();
        string line = $"t={t,5:0}s connected={connected}/{clients.Count} joined={joined} reconnects={reconnects} sessionChanges={changes} pawnLosses={losses} rejected={rejections} " +
                      $"in={dpin} pkt/s {dbin * 8 / 1e6:0.00} Mb/s out={dpout} pkt/s {dbout * 8 / 1e6:0.00} Mb/s rtt p50={p50:0.0} p95={p95:0.0} ms gateways[{string.Join(" ", gateways)}]";
        Console.WriteLine(line);
        csv?.WriteLine(string.Join(",", t.ToString("0", CultureInfo.InvariantCulture), connected, joined, reconnects, changes, losses, rejections, dpin, dpout, dbin, dbout, p50.ToString("0.0", CultureInfo.InvariantCulture), p95.ToString("0.0", CultureInfo.InvariantCulture), string.Join(" ", gateways)));
        if (o.Verbose)
        {
            foreach (var c in clients.Where(c => c.LastError.Length > 0).Take(5)) Console.WriteLine($"  {c.Name}: {c.LastError}");
        }
    }

    private static Options? Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(a + " needs a value");
            try
            {
                switch (a)
                {
                    case "--gateway":
                    {
                        string v = Next(); int colon = v.LastIndexOf(':');
                        if (colon > 0) { o.Host = v.Substring(0, colon); o.Port = int.Parse(v.Substring(colon + 1), CultureInfo.InvariantCulture); }
                        else o.Host = v;
                        break;
                    }
                    case "--clients": o.Clients = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--seconds": o.Seconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--ramp": o.RampSeconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--input-hz": o.InputHz = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--name-prefix": o.NamePrefix = Next(); break;
                    case "--bot": o.Bot = true; break;
                    case "--token": o.Token = Next(); break;
                    case "--reconnect-every": o.ReconnectEvery = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--csv": o.Csv = Next(); break;
                    case "--verbose": o.Verbose = true; break;
                    case "--help": case "-h": Usage(); return null;
                    default: Console.Error.WriteLine("unknown option " + a); Usage(); return null;
                }
            }
            catch (Exception e) { Console.Error.WriteLine(e.Message); Usage(); return null; }
        }
        return o;
    }

    private static void Usage()
    {
        Console.WriteLine("nebula-loadgen --gateway <host:port> --clients <n> --seconds <s> [--ramp <s>] [--input-hz <n>] [--name-prefix <p>] [--bot] [--token <identity token>] [--reconnect-every <s>] [--csv <file>] [--verbose]");
        Console.WriteLine("Synthetic clients for a gateway fleet: connect, send inputs, reconnect with the session token when told to, and report per second. Exit 0 when no session id changed and no pawn was lost across reconnects.");
    }
}
