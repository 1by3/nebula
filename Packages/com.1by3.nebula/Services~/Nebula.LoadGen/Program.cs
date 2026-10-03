using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Nebula;
using V2 = System.Numerics.Vector2;
using V3 = System.Numerics.Vector3;

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
/// Clients take an anonymous identity each unless <c>--token</c> gives them all the same one, which needs a mesh
/// started with <c>-nebula-single-session false</c> (<see cref="NebulaConfig.SingleSessionPerPlayer"/>).
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
        /// <summary>The game content version clients announce (<c>--content-version</c>); a gateway refuses clients whose version it doesn't accept (NebulaConfig.GameContentVersion).</summary>
        public uint ContentVersion;
        /// <summary>Fraction of clients that never send a focus hint and never move their focus: idle players.</summary>
        public double Idle;
        public int Seed = 1;
        public double MaxMinutes;
        public double MoveSpeed = 5;
        public int IdOffset;
        public string? TokensFile, StatsFile, ProfilePath;
        public string? MixText;
        public List<string> Plugins = new();
        public List<(string Name, string Key, string Value)> Opts = new();
        public bool InputHzSet, SecondsSet, SeedSet, MoveSpeedSet;
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
        private string _lastError = "";
        /// <summary>Set by any failure or drop; <see cref="LastErrorSeq"/> counts the sets so the verbose report prints each once, not every second.</summary>
        public string LastError { get => _lastError; set { _lastError = value; LastErrorSeq++; } }
        public long LastErrorSeq, LastErrorPrinted;
        public readonly List<double> Rtts = new();
        public double NextReconnectDrill;
        /// <summary>
        /// Replicas this client holds. With interest management this is the number that says whether the mesh
        /// scopes: it must follow the local density of the world and not its size.
        /// </summary>
        public readonly HashSet<ulong> Replicas = new();
        public readonly Dictionary<ulong, ushort> ViewSeq = new();
        public long BytesIn;
        public long LastBytesIn;
        /// <summary>This client stands still and sends no focus hint (<c>--idle</c>).</summary>
        public bool Idle;
        // Behaviour (NEB-365).
        public string BehaviourName = "idle";
        public ILoadGenBehaviour Behaviour = null!;
        public ClientContext Ctx = null!;
        public Random Rng = null!;
        public readonly Dictionary<ulong, ReplicaInfo> Info = new();
        public readonly Dictionary<ulong, uint> Epochs = new();
        public V2 Move; public bool Sprint; public uint ActionBits; public byte[]? RawInput;
        public V3 EstimatedPosition; public bool HasEstimate;
        public bool Notified; // OnJoined has run for this session
        public bool Quit; public double RejoinAt = -1;
        public double LastTickAt;
        public bool FocusSet; public byte FocusGeneration; public bool FocusSend; public double FocusX, FocusY, FocusZ; public bool FocusClear;
        public long Inputs, Rpcs, Travels, CustomActions, Errors, LeaveCount;
        public long LastInputs, LastRpcs, LastTravels, LastActions;
        public int Left;
        public bool Leaving; public double LeaveStarted, LeaveRejoin;
        public long LastBytesInB;

        public Client(int index, string name, string identityToken)
        {
            Index = index; Name = name; IdentityToken = identityToken;
            Transport = new LiteNetTransport("loadgen-" + index);
        }
    }

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double Now => Clock.Elapsed.TotalSeconds;
    private static long _packetsIn, _packetsOut, _bytesIn, _bytesOut;
    private static BehaviourRegistry _registry = null!;
    private static double _runStart, _lastBehaviourReport;
    private static StreamWriter? _stats;
    private static bool _statsCsv;

    public static int Main(string[] args)
    {
        var o = Parse(args);
        if (o == null) return 2;
        var setup = Setup(o);
        if (setup == null) return 2;
        var (registry, mix, assignment) = setup.Value;
        _registry = registry;
        if (o.MaxMinutes > 0 && o.MaxMinutes * 60 < o.Seconds) o.Seconds = o.MaxMinutes * 60;
        string[] tokens = o.TokensFile != null ? File.ReadAllLines(o.TokensFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray() : Array.Empty<string>();
        if (o.TokensFile != null && tokens.Length < o.Clients) { Console.Error.WriteLine($"--tokens-file has {tokens.Length} tokens for {o.Clients} clients"); return 2; }
        Console.WriteLine($"nebula-loadgen: mix {string.Join(", ", mix.Entries.Select((e, i) => $"{e.Name}={assignment.Count(a => a == i)}"))} seed {o.Seed}");
        Console.WriteLine($"nebula-loadgen: {o.Clients} clients -> {o.Host}:{o.Port} for {o.Seconds:0} s (ramp {o.RampSeconds:0} s, inputs {o.InputHz} Hz{(o.ReconnectEvery > 0 ? $", reconnect drill every {o.ReconnectEvery:0} s" : "")})");
        var clients = new List<Client>();
        for (int i = 0; i < o.Clients; i++)
        {
            // A mesh where every synthetic client moves is not the mesh a game has: standing players are the
            // ones whose replica count and bytes should be flat, and a regression shows up in them first.
            int id = o.IdOffset + i;
            var client = new Client(id, $"{o.NamePrefix}{id}", o.TokensFile != null ? tokens[i] : o.Token) { Idle = o.Idle > 0 && id % 100 < o.Idle * 100 };
            var entry = mix.Entries[assignment[i]];
            client.BehaviourName = entry.Name;
            client.Rng = new Random(BehaviourMix.ClientSeed(o.Seed, id));
            client.Behaviour = registry.Create(entry.Name, entry.Options);
            client.Ctx = new ClientContext(client, o);
            clients.Add(client);
        }
        // One token is one player, and a mesh with NebulaConfig.SingleSessionPerPlayer on (the default) keeps only
        // that player's newest connection: the rest are closed as soon as they are welcomed.
        if (o.Clients > 1 && o.Token.Length > 0)
            Console.WriteLine("nebula-loadgen: warning: --token makes every client the same player. Give the mesh -nebula-single-session false, or a token per client, or the gateway will keep only the newest connection.");
        StreamWriter? csv = o.Csv != null ? new StreamWriter(o.Csv) : null;
        csv?.WriteLine("t,connected,joined,reconnects,sessionChanges,pawnLosses,rejections,packetsIn,packetsOut,bytesIn,bytesOut,rttP50,rttP95,replicasAvg,replicasMax,bytesPerClientAvg,bytesPerClientMax,gateways");

        if (o.StatsFile != null)
        {
            _stats = new StreamWriter(o.StatsFile);
            _statsCsv = o.StatsFile.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
            if (_statsCsv) _stats.WriteLine(BehaviourReportRow.CsvHeader);
        }
        double start = Now, nextReport = start + 1, end = start + o.Seconds;
        _runStart = start; _lastBehaviourReport = start;
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
                if (c.Peer < 0 && !c.Quit && now >= Math.Max(due, c.ReconnectAt)) Connect(c, o);
                if (c.Leaving && now - c.LeaveStarted > 3) FinishLeave(c);
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
                    if (now >= c.NextInput && !c.Leaving)
                    {
                        c.NextInput = now + 1.0 / o.InputHz;
                        RunBehaviour(c, now, o);
                        if (c.Leaving) { c.Transport.Flush(); continue; }
                        writer.Reset();
                        new ClientInputMsg { ClientId = c.SessionId, Frames = new List<ClientInputMsg.Frame> { new() { Tick = ++c.Tick, Payload = InputPayload(c) } } }.Write(writer, MsgId.ClientInput);
                        Send(c, Delivery.Sequenced, writer.ToSegment());
                        c.Inputs++;
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
        _stats?.Dispose();
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

    private static void FinishLeave(Client c)
    {
        double rejoin = c.LeaveRejoin;
        c.Leaving = false;
        Drop(c, "left", rejoin < 0 ? 1e9 : rejoin);
        c.SessionToken = ""; c.Welcome = null; c.Pawn = 0; c.HasEstimate = false;
        c.Left++;
        if (rejoin < 0) c.Quit = true;
    }

    private static void RunBehaviour(Client c, double now, Options o)
    {
        double dt = c.LastTickAt > 0 ? now - c.LastTickAt : 1.0 / o.InputHz;
        c.LastTickAt = now;
        if (c.Join == JoinState.Joined && !c.Notified)
        {
            c.Notified = true;
            if (!c.HasEstimate && c.Info.TryGetValue(c.Pawn, out var pawn)) { c.EstimatedPosition = pawn.SpawnPosition; c.HasEstimate = true; }
            Guard(c, () => c.Behaviour.OnJoined(c.Ctx));
        }
        if (c.Leaving) return;
        // Dead reckoning of the last move at the configured speed; a behaviour can correct it.
        var move = c.Move;
        if (move.LengthSquared() > 1) move = V2.Normalize(move);
        float step = (float)(o.MoveSpeed * (c.Sprint ? 1.6 : 1) * dt);
        c.EstimatedPosition += new V3(move.X * step, 0, move.Y * step);
        Guard(c, () => c.Behaviour.Tick(c.Ctx, dt));
        if (c.FocusSend)
        {
            c.FocusSend = false;
            var w = new NetworkWriter(64);
            new ClientFocusHintMsg { X = c.FocusX, Y = c.FocusY, Z = c.FocusZ, Generation = c.FocusGeneration, Clear = c.FocusClear }.Write(w);
            Send(c, c.FocusClear ? Delivery.ReliableOrdered : Delivery.Sequenced, w.ToSegment());
        }
    }

    private static void Guard(Client c, Action a)
    {
        try { a(); }
        catch (Exception ex) { c.Errors++; c.LastError = "behaviour " + c.BehaviourName + ": " + ex.Message; }
    }

    private static void Drop(Client c, string why, double retryIn)
    {
        if (c.Notified) { c.Notified = false; Guard(c, () => c.Behaviour.OnLeft(c.Ctx)); }
        c.Info.Clear(); c.Epochs.Clear(); c.Leaving = false;
        if (c.Peer >= 0) { try { c.Transport.Disconnect(c.Peer); } catch { } }
        c.Peer = -1;
        c.Connected = false;
        c.Join = JoinState.None;
        c.Replicas.Clear();
        c.ViewSeq.Clear();
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
                new HelloMsg { Role = PeerRole.Client, Id = c.Name, Flags = o.Bot ? HelloFlags.Bot : HelloFlags.None, Token = c.IdentityToken, Session = c.SessionToken, GameContentVersion = o.ContentVersion }.Write(writer);
                Send(c, Delivery.ReliableOrdered, writer.ToSegment());
                break;
            case TransportEvent.Kind.Disconnected:
                if (c.Leaving) FinishLeave(c);
                else if (c.Peer >= 0)
                {
                    c.Reconnects++;
                    Drop(c, "link dropped", 1.0);
                }
                break;
            case TransportEvent.Kind.Data:
                Interlocked.Increment(ref _packetsIn);
                Interlocked.Add(ref _bytesIn, e.Data.Count);
                c.BytesIn += e.Data.Count;
                reader.Set(e.Data);
                try { Dispatch(c, reader, o); } catch (Exception ex) { c.LastError = "bad packet: " + ex.Message; c.Errors++; }
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
                if (!w.Reclaimed) { c.Pawn = 0; c.HasEstimate = false; }
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
                c.Replicas.Add(spawn.NetId);
                c.Epochs[spawn.NetId] = spawn.Epoch;
                {
                    if (!c.Info.TryGetValue(spawn.NetId, out var info)) c.Info[spawn.NetId] = info = new ReplicaInfo { NetId = spawn.NetId };
                    info.PrefabId = spawn.PrefabId; info.OwnerClientId = spawn.OwnerClientId;
                    info.SpawnPosition = new V3(spawn.LocalPosition.x, spawn.LocalPosition.y, spawn.LocalPosition.z);
                    if (spawn.Vars != null) { info.Vars = spawn.Vars; info.VarsVersion++; }
                    if (spawn.NetId == c.Pawn && !c.HasEstimate) { c.EstimatedPosition = info.SpawnPosition; c.HasEstimate = true; }
                }
                if (spawn.ViewSeq != 0) c.ViewSeq[spawn.NetId] = spawn.ViewSeq;
                break;
            }
            case MsgId.EntityDespawn:
            {
                var despawn = EntityDespawnMsg.Read(r);
                // The same view guard a real client applies (design D4).
                if (despawn.ViewSeq != 0 && c.ViewSeq.TryGetValue(despawn.NetId, out ushort held) && despawn.ViewSeq < held) break;
                c.ViewSeq.Remove(despawn.NetId);
                c.Replicas.Remove(despawn.NetId);
                c.Info.Remove(despawn.NetId); c.Epochs.Remove(despawn.NetId);
                if (despawn.NetId == c.Pawn) c.Pawn = 0;
                break;
            }
            case MsgId.EntityVars:
            {
                var vars = EntityVarsMsg.Read(r);
                if (c.Info.TryGetValue(vars.NetId, out var info) && vars.Vars != null) { info.Vars = vars.Vars; info.VarsVersion++; }
                c.Epochs[vars.NetId] = vars.Epoch;
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

    private static readonly byte[] EncodeBuffer = new byte[256];
    private static byte[] InputPayload(Client c)
    {
        // The game decodes inputs; a gateway forwards bytes. The default encoder varies them so nothing upstream
        // can dedupe; a plugin's encoder writes the game's own input.
        if (c.RawInput != null) return c.RawInput;
        int n = _registry.InputEncoder.Encode(new InputSnapshot { Tick = c.Tick, ClientIndex = c.Index, Move = c.Move, Sprint = c.Sprint, Actions = c.ActionBits }, EncodeBuffer);
        return n == EncodeBuffer.Length ? EncodeBuffer : EncodeBuffer.AsSpan(0, n).ToArray();
    }

    /// <summary>What a behaviour sees of one client (see <see cref="IClientContext"/>).</summary>
    private sealed class ClientContext : IClientContext
    {
        private readonly Client _c;
        private readonly Options _o;
        private static readonly NetworkWriter RpcWriter = new NetworkWriter(512);

        public ClientContext(Client c, Options o) { _c = c; _o = o; }

        public int Index => _c.Index;
        public string Name => _c.Name;
        public string BehaviourName => _c.BehaviourName;
        public double Time => Now - _runStart;
        public Random Rng => _c.Rng;
        public bool Connected => _c.Connected;
        public bool Joined => _c.Join == JoinState.Joined;
        public ulong SessionId => _c.SessionId;
        public ulong PawnNetId => _c.Pawn;
        public ReplicaInfo? Pawn => _c.Info.TryGetValue(_c.Pawn, out var p) ? p : null;
        public IReadOnlyCollection<ulong> Replicas => _c.Replicas;
        public bool TryGetReplica(ulong netId, out ReplicaInfo replica) => _c.Info.TryGetValue(netId, out replica!);
        public V3 EstimatedPosition => _c.EstimatedPosition;
        public void SetEstimatedPosition(V3 position) { _c.EstimatedPosition = position; _c.HasEstimate = true; }
        public void SetMove(V2 direction, bool sprint = false) { _c.Move = direction.LengthSquared() > 1 ? V2.Normalize(direction) : direction; _c.Sprint = sprint; }
        public void SetActions(uint bits) => _c.ActionBits = bits;
        public void SetRawInput(byte[]? payload) => _c.RawInput = payload;

        public void SendServerRpc(ulong netId, byte behaviourIndex, string method, ReadOnlySpan<byte> args)
        {
            uint h = 2166136261;
            unchecked { foreach (char ch in method) { h ^= ch; h *= 16777619; } }
            SendServerRpc(netId, behaviourIndex, h, args);
        }

        public void SendServerRpc(ulong netId, byte behaviourIndex, uint methodHash, ReadOnlySpan<byte> args)
        {
            if (netId == 0) netId = _c.Pawn;
            if (netId == 0 || _c.Peer < 0) { ReportError("rpc: no target entity or no link"); return; }
            RpcWriter.Reset();
            new EntityRpcMsg { NetId = netId, Epoch = _c.Epochs.TryGetValue(netId, out var e) ? e : 0, BehaviourIndex = behaviourIndex, MethodHash = methodHash, ClientId = _c.SessionId, Args = args.ToArray() }.Write(RpcWriter, MsgId.ServerRpc);
            Send(_c, Delivery.ReliableOrdered, RpcWriter.ToSegment());
            _c.Rpcs++;
        }

        public bool RequestScopeTravel(string target)
        {
            var handler = _registry.ScopeTravel;
            if (handler == null) { ReportError("scope travel: no handler registered (a plugin sets one with SetScopeTravel)"); return false; }
            bool ok;
            try { ok = handler(this, target); }
            catch (Exception ex) { ReportError("scope travel: " + ex.Message); return false; }
            if (ok) _c.Travels++; else _c.Errors++;
            return ok;
        }

        public void SetFocusHint(double x, double y, double z) { _c.FocusX = x; _c.FocusY = y; _c.FocusZ = z; _c.FocusClear = false; _c.FocusSend = true; _c.FocusSet = true; }
        public void ClearFocusHint() { if (!_c.FocusSet) return; _c.FocusSet = false; _c.FocusGeneration++; _c.FocusClear = true; _c.FocusSend = true; }

        public void Leave(double rejoinAfterSeconds)
        {
            if (_c.Leaving || _c.Peer < 0) return;
            RpcWriter.Reset();
            new GoodbyeMsg().Write(RpcWriter);
            Send(_c, Delivery.ReliableOrdered, RpcWriter.ToSegment());
            _c.Leaving = true; _c.LeaveStarted = Now; _c.LeaveRejoin = rejoinAfterSeconds;
        }

        public void CountAction() => _c.CustomActions++;
        public void ReportError(string message) { _c.Errors++; _c.LastError = message; }
    }

    private static (BehaviourRegistry, BehaviourMix, int[])? Setup(Options o)
    {
        try
        {
            var registry = new BehaviourRegistry();
            LoadProfile? profile = o.ProfilePath != null ? LoadProfile.Parse(File.ReadAllText(o.ProfilePath)) : null;
            if (profile != null)
            {
                if (profile.Seed != null && !o.SeedSet) o.Seed = profile.Seed.Value;
                if (profile.MaxMinutes != null && o.MaxMinutes == 0) o.MaxMinutes = profile.MaxMinutes.Value;
                if (profile.InputHz != null && !o.InputHzSet) o.InputHz = profile.InputHz.Value;
                if (profile.MoveSpeed != null && !o.MoveSpeedSet) o.MoveSpeed = profile.MoveSpeed.Value;
                foreach (string p in profile.Plugins) o.Plugins.Add(p);
            }
            foreach (string path in o.Plugins)
            {
                // A relative path in a profile is relative to the profile.
                string full = !System.IO.Path.IsPathRooted(path) && profile != null && !File.Exists(path) ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(o.ProfilePath!))!, path) : path;
                registry.LoadPlugin(full);
                Console.WriteLine("nebula-loadgen: loaded plugin " + full);
            }
            BehaviourMix mix = o.MixText != null ? BehaviourMix.Parse(o.MixText) : profile?.Mix ?? BehaviourMix.Parse("idle");
            foreach (var entry in mix.Entries)
            {
                if (!registry.Has(entry.Name)) throw new ArgumentException($"unknown behaviour '{entry.Name}' (known: {string.Join(", ", registry.Names)})");
                foreach (var (name, key, value) in o.Opts) if (name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)) entry.Options.Set(key, value);
            }
            foreach (var (name, _, _) in o.Opts)
                if (!mix.Entries.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"--opt names '{name}', which is not in the mix");
            return (registry, mix, mix.Assign(o.Clients, o.Seed));
        }
        catch (Exception e) when (e is ArgumentException or IOException or JsonException or InvalidOperationException or BadImageFormatException)
        {
            Console.Error.WriteLine(e.Message);
            return null;
        }
    }

    private static void ReportBehaviours(List<Client> clients, double t, Options o)
    {
        double interval = Math.Max(0.001, Now - _lastBehaviourReport);
        _lastBehaviourReport = Now;
        foreach (var group in clients.GroupBy(c => c.BehaviourName).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var list = group.ToList();
            var rtts = list.SelectMany(c => c.Rtts).OrderBy(x => x).ToList();
            long inputs = 0, rpcs = 0, travels = 0, custom = 0, bytes = 0;
            foreach (var c in list)
            {
                inputs += c.Inputs - c.LastInputs; c.LastInputs = c.Inputs;
                rpcs += c.Rpcs - c.LastRpcs; c.LastRpcs = c.Rpcs;
                travels += c.Travels - c.LastTravels; c.LastTravels = c.Travels;
                custom += c.CustomActions - c.LastActions; c.LastActions = c.CustomActions;
                bytes += c.BytesIn - c.LastBytesInB; c.LastBytesInB = c.BytesIn;
            }
            var withWelcome = list.Where(c => c.Welcome != null).ToList();
            var row = new BehaviourReportRow
            {
                T = t, Behaviour = group.Key, Clients = list.Count,
                Connected = list.Count(c => c.Connected), Joined = list.Count(c => c.Join == JoinState.Joined),
                InputsPerSec = inputs / interval, RpcsPerSec = rpcs / interval, TravelsPerSec = travels / interval,
                ActionsPerSec = (rpcs + travels + custom) / interval,
                Actions = list.Sum(c => c.Rpcs + c.Travels + c.CustomActions), Rpcs = list.Sum(c => c.Rpcs), Errors = list.Sum(c => c.Errors),
                Reconnects = list.Sum(c => c.Reconnects), Rejections = list.Sum(c => c.Rejections), SessionChanges = list.Sum(c => c.SessionChanges), SessionsLeft = list.Sum(c => c.Left),
                RttP50 = rtts.Count > 0 ? rtts[rtts.Count / 2] : 0, RttP95 = rtts.Count > 0 ? rtts[(int)(rtts.Count * 0.95)] : 0,
                ReplicasAvg = withWelcome.Count > 0 ? withWelcome.Average(c => c.Replicas.Count) : 0,
                BytesInPerSec = list.Count > 0 ? bytes / interval / list.Count : 0,
            };
            _stats?.WriteLine(_statsCsv ? row.ToCsv() : row.ToJson());
        }
        _stats?.Flush();
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
        // Per-client replicas and bytes: the two numbers interest management is judged on. A total hides a
        // single client being sent the world behind the many that are not.
        var joinedClients = clients.Where(c => c.Welcome != null).ToList();
        double replicasAvg = joinedClients.Count > 0 ? joinedClients.Average(c => c.Replicas.Count) : 0;
        int replicasMax = joinedClients.Count > 0 ? joinedClients.Max(c => c.Replicas.Count) : 0;
        double bytesAvg = 0, bytesMax = 0;
        foreach (var c in joinedClients)
        {
            double delta = c.BytesIn - c.LastBytesIn;
            c.LastBytesIn = c.BytesIn;
            bytesAvg += delta;
            if (delta > bytesMax) bytesMax = delta;
        }
        if (joinedClients.Count > 0) bytesAvg /= joinedClients.Count;
        string line = $"t={t,5:0}s connected={connected}/{clients.Count} joined={joined} reconnects={reconnects} sessionChanges={changes} pawnLosses={losses} rejected={rejections} " +
                      $"in={dpin} pkt/s {dbin * 8 / 1e6:0.00} Mb/s out={dpout} pkt/s {dbout * 8 / 1e6:0.00} Mb/s rtt p50={p50:0.0} p95={p95:0.0} ms " +
                      $"replicas avg={replicasAvg:0.0} max={replicasMax} bytes/client avg={bytesAvg / 1024:0.0} max={bytesMax / 1024:0.0} KB/s gateways[{string.Join(" ", gateways)}]";
        Console.WriteLine(line);
        csv?.WriteLine(string.Join(",", t.ToString("0", CultureInfo.InvariantCulture), connected, joined, reconnects, changes, losses, rejections, dpin, dpout, dbin, dbout, p50.ToString("0.0", CultureInfo.InvariantCulture), p95.ToString("0.0", CultureInfo.InvariantCulture), replicasAvg.ToString("0.0", CultureInfo.InvariantCulture), replicasMax, bytesAvg.ToString("0", CultureInfo.InvariantCulture), bytesMax.ToString("0", CultureInfo.InvariantCulture), string.Join(" ", gateways)));
        ReportBehaviours(clients, t, o);
        if (o.Verbose)
        {
            // Each failure is printed once, when it happens. A client's LastError stays set after it recovers, so printing
            // the field every second repeated the first five clients' old drops for the whole run.
            foreach (var c in clients.Where(c => c.LastErrorSeq != c.LastErrorPrinted).Take(10))
            {
                c.LastErrorPrinted = c.LastErrorSeq;
                Console.WriteLine($"  {c.Name}: {c.LastError}");
            }
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
                    case "--input-hz": o.InputHz = int.Parse(Next(), CultureInfo.InvariantCulture); o.InputHzSet = true; break;
                    case "--name-prefix": o.NamePrefix = Next(); break;
                    case "--bot": o.Bot = true; break;
                    case "--token": o.Token = Next(); break;
                    case "--reconnect-every": o.ReconnectEvery = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--csv": o.Csv = Next(); break;
                    case "--idle": o.Idle = Math.Clamp(double.Parse(Next(), CultureInfo.InvariantCulture), 0, 1); break;
                    case "--verbose": o.Verbose = true; break;
                    case "--content-version": o.ContentVersion = uint.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--behaviour": case "--behavior": o.MixText = Next(); break;
                    case "--mix": o.MixText = Next(); break;
                    case "--opt":
                    {
                        string v = Next(); int eq = v.IndexOf('='), dot = v.IndexOf('.');
                        if (eq < 0 || dot < 1 || dot > eq) throw new ArgumentException("--opt wants name.key=value, got " + v);
                        o.Opts.Add((v.Substring(0, dot), v.Substring(dot + 1, eq - dot - 1), v.Substring(eq + 1)));
                        break;
                    }
                    case "--profile": o.ProfilePath = Next(); break;
                    case "--plugin": o.Plugins.Add(Next()); break;
                    case "--seed": o.Seed = int.Parse(Next(), CultureInfo.InvariantCulture); o.SeedSet = true; break;
                    case "--max-minutes": o.MaxMinutes = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--move-speed": o.MoveSpeed = double.Parse(Next(), CultureInfo.InvariantCulture); o.MoveSpeedSet = true; break;
                    case "--id-offset": o.IdOffset = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--tokens-file": o.TokensFile = Next(); break;
                    case "--behaviour-stats": o.StatsFile = Next(); break;
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
        Console.WriteLine("nebula-loadgen --gateway <host:port> --clients <n> --seconds <s> [--ramp <s>] [--input-hz <n>] [--name-prefix <p>] [--bot] [--token <identity token>] [--reconnect-every <s>] [--idle <0..1>] [--csv <file>] [--content-version <n>] [--verbose]");
        Console.WriteLine("Behaviours: [--behaviour <name> | --mix name:weight,...] [--opt name.key=value]... [--profile <json>] [--plugin <dll>]... [--seed <n>] [--max-minutes <m>] [--move-speed <m/s>] [--id-offset <n>] [--tokens-file <file>] [--behaviour-stats <file.jsonl|file.csv>]");
        Console.WriteLine("Built-in behaviours: idle, wander, path, burst, travel, rpc, churn. See the load-testing guide for each one's options and for writing a plugin.");
        Console.WriteLine("--idle is the fraction of clients that stand still and send no focus hint; their replica count and bytes are what must stay flat as the world grows.");
        Console.WriteLine("Synthetic clients for a gateway fleet: connect, send inputs, reconnect with the session token when told to, and report per second. Exit 0 when no session id changed and no pawn was lost across reconnects.");
        Console.WriteLine("--token gives every client the same identity, so it needs a mesh started with -nebula-single-session false. Without it, clients get an anonymous identity each.");
    }
}
