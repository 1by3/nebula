using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nebula
{
    // Admission coordination is deliberately separate from topology snapshots: workers do not receive
    // the player directory, and gateways poll only their own outstanding disconnect requests.
    /// <summary>
    /// Optional control-plane extension for single-session gateway admission. Serialize claims per identity
    /// and grant a takeover only after the previous gateway releases its claim. Failure blocks admission
    /// without disconnecting existing clients. Keep session records separate from topology snapshots.
    /// </summary>
    public interface IGatewaySessionControlPlane
    {
        /// <summary>Submit an idempotent operation. Invoke the callback on the caller's main thread, normally from <see cref="IControlPlane.Tick"/>. Return an error reply on failure.</summary>
        void SessionRequest(GatewaySessionRequest request, Action<GatewaySessionReply> complete);
    }

    internal interface IGatewaySessionStore
    {
        string LoadSession(string identity);
        void SaveSession(string identity, string value);
        void ClearSessions();
    }

    /// <summary>An operation on one player's admission claim. Transport must preserve all 64 bits of each ID.</summary>
    public sealed class GatewaySessionRequest
    {
        /// <summary>claim, poll, release, cancel, or reserve. Poll returns disconnect requests for the named gateway.</summary>
        public string Operation = "";
        /// <summary>Authenticated player identity; empty only for poll.</summary>
        public string Identity = "";
        /// <summary>Gateway ID and process incarnation, formatted by <see cref="PlayerSessions.GatewayKey"/>.</summary>
        public string Gateway = "";
        /// <summary>Unique ID for this connection's claim. Retries reuse it; releases match both gateway and claim.</summary>
        public string Claim = "";
        /// <summary>Worker reserved for a pending spawn or currently owning the pawn.</summary>
        public string Worker = "";
        /// <summary>Proposed ID on an initial claim. The coordinator preserves an existing session's ID.</summary>
        public ulong SessionId;
        /// <summary>Minimum generation from a verified token. A replacement receives a strictly newer generation.</summary>
        public ulong Generation;
        /// <summary>Container for the spawn reservation or observed pawn.</summary>
        public ContainerRef Container = ContainerRef.None;
        /// <summary>The route came from an existing authoritative pawn announcement rather than a proposed spawn.</summary>
        public bool ObservedPawn;
    }

    /// <summary>The result of a gateway admission operation.</summary>
    public sealed class GatewaySessionReply
    {
        /// <summary>granted, pending, ok, gone, stale, or error. Only granted permits admission or a spawn reservation.</summary>
        public string Status = "";
        /// <summary>Player-readable failure reason for an error result.</summary>
        public string Error = "";
        /// <summary>The granted session and reserved route, when present.</summary>
        public GatewaySessionRequest Session;
        /// <summary>Connections to disconnect before acknowledging their release. Returned by poll.</summary>
        public readonly List<GatewaySessionRequest> Revocations = new List<GatewaySessionRequest>();
    }

    internal sealed class GatewaySessionDirectory
    {
        internal const string Path = "/api/gateway-sessions";
        /// <summary>
        /// A pending claim on a gateway that has stopped heartbeating waits this long past
        /// <see cref="Entry.PendingUntil"/> before its owner's claim is evicted (D7a / NEB-229). Matches
        /// <c>NebulaConfig.WorkerTimeoutSeconds</c>'s default: the gateway row itself is considered gone once its
        /// last heartbeat is older than this, the same cutoff <see cref="ControlPlaneExtensions.IsWorkerAlive"/>
        /// uses for a worker row. A caller that knows the mesh's actual <c>WorkerTimeoutSeconds</c> may set
        /// <see cref="GatewayStaleAfterSeconds"/> instead of relying on the default.
        /// </summary>
        internal const double DefaultGatewayStaleAfterSeconds = 5.0;
        private sealed class Entry
        {
            public GatewaySessionRequest Owner;
            public GatewaySessionRequest Pending;
            public DateTime PendingUntil;
            public DateTime RetainUntil;
        }

        private readonly Dictionary<string, Entry> _players = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _revocations = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Queue<KeyValuePair<string, DateTime>> _retired = new Queue<KeyValuePair<string, DateTime>>();
        private readonly Func<DateTime> _now;
        private readonly Func<string, bool> _workerAlive;
        private readonly Func<string, bool> _gatewayAlive;
        private readonly object _gate = new object();
        internal IGatewaySessionStore Store;

        internal void Reset()
        {
            lock (_gate)
            {
                Store?.ClearSessions();
                _players.Clear(); _revocations.Clear(); _retired.Clear();
            }
        }

        /// <param name="workerAlive">True while the named worker is still registered and not dead.</param>
        /// <param name="now">Clock seam for tests.</param>
        /// <param name="gatewayAlive">
        /// True while the gateway named by a <see cref="PlayerSessions.GatewayKey"/> (id + incarnation) is still
        /// heartbeating under that same incarnation. Null means every gateway is assumed alive, which reproduces
        /// the pre-NEB-229 behaviour (a killed gateway strands its sessions forever; see D7a).
        /// </param>
        internal GatewaySessionDirectory(Func<string, bool> workerAlive, Func<DateTime> now = null, Func<string, bool> gatewayAlive = null)
        {
            _workerAlive = workerAlive;
            _now = now ?? (() => DateTime.UtcNow);
            _gatewayAlive = gatewayAlive;
        }

        internal GatewaySessionReply Handle(GatewaySessionRequest request)
        {
            lock (_gate)
            {
                if (request.Operation == "poll") return HandleCore(request);
                string identity = request.Identity;
                if (string.IsNullOrEmpty(identity)) return HandleCore(request);
                try
                {
                    if (!_players.ContainsKey(identity) && Store != null)
                    {
                        string saved = Store.LoadSession(identity);
                        if (!string.IsNullOrEmpty(saved))
                        {
                            var restored = Decode(saved);
                            if (restored.Owner.Gateway.Length > 0 || restored.RetainUntil > _now())
                            {
                                _players[identity] = restored;
                                AddRevocation(restored);
                            }
                        }
                    }
                }
                catch (Exception) { return Failure("session coordination storage is unavailable"); }
                _players.TryGetValue(identity, out var before);
                string previous = before == null ? null : Encode(before);
                var reply = HandleCore(request);
                _players.TryGetValue(identity, out var after);
                string next = after == null ? null : Encode(after);
                if (previous == next || Store == null) return reply;
                try { Store.SaveSession(identity, next); }
                catch (Exception)
                {
                    if (after != null) RemoveRevocation(after.Owner);
                    if (previous == null) _players.Remove(identity);
                    else { var restored = Decode(previous); _players[identity] = restored; AddRevocation(restored); }
                    return Failure("session coordination storage is unavailable");
                }
                return reply;
            }
        }

        private GatewaySessionReply HandleCore(GatewaySessionRequest request)
        {
            lock (_gate)
            {
                var now = _now();
                for (int i = 0; i < 64 && _retired.Count > 0 && _retired.Peek().Value <= now; i++)
                {
                    var expired = _retired.Dequeue();
                    if (_players.TryGetValue(expired.Key, out var old) && old.Owner.Gateway.Length == 0 &&
                        old.Pending == null && old.RetainUntil == expired.Value)
                    {
                        try { Store?.SaveSession(expired.Key, null); }
                        catch (Exception)
                        {
                            _retired.Enqueue(expired);
                            break;
                        }
                        _players.Remove(expired.Key);
                    }
                }
                if (string.IsNullOrEmpty(request.Gateway)) return Failure("gateway is required");
                if (request.Operation == "poll")
                {
                    var reply = new GatewaySessionReply { Status = "ok" };
                    if (_revocations.TryGetValue(request.Gateway, out var identities))
                    {
                        var expired = new List<string>();
                        int examined = 0;
                        foreach (var identity in identities)
                        {
                            if (examined++ == 256) break;
                            if (_players.TryGetValue(identity, out var held) && held.Pending != null && held.PendingUntil > now)
                                reply.Revocations.Add(Copy(held.Owner));
                            else expired.Add(identity);
                        }
                        foreach (var identity in expired) identities.Remove(identity);
                        if (identities.Count == 0) _revocations.Remove(request.Gateway);
                    }
                    return reply;
                }
                if (string.IsNullOrEmpty(request.Identity) || string.IsNullOrEmpty(request.Claim))
                    return Failure("identity and claim are required");
                _players.TryGetValue(request.Identity, out var entry);
                if (entry != null && entry.Pending != null && entry.PendingUntil <= now)
                {
                    // The waiting claim outlived its 15 s coordination window. If the owner is still heartbeating
                    // (just slow), drop the stale pending claim and make the caller re-claim and re-wait — the
                    // owner might still release normally. If the owner's gateway is gone (never released, no
                    // Dispose, a hard kill: D7a), nobody will ever release it, so transfer ownership to the
                    // waiting claimant now instead of re-pending forever. The incarnation check inside
                    // _gatewayAlive means a slow-but-alive owner is never evicted by a same-key retry racing it.
                    bool ownerGone = _gatewayAlive != null && entry.Owner.Gateway.Length > 0 && !_gatewayAlive(entry.Owner.Gateway);
                    var evicted = entry.Pending;
                    RemoveRevocation(entry.Owner);
                    entry.Pending = null;
                    if (ownerGone) Activate(entry, evicted);
                }
                if (request.Operation == "claim")
                {
                    if (request.Generation == ulong.MaxValue || (entry != null && entry.Owner.Generation == ulong.MaxValue))
                        return Failure("session generation is exhausted");
                    if (entry == null)
                    {
                        if (request.SessionId == 0 || request.Generation == ulong.MaxValue) return Failure("invalid session");
                        entry = new Entry { Owner = Copy(request) };
                        entry.Owner.Generation = Math.Max(1UL, request.Generation + 1);
                        _players.Add(request.Identity, entry);
                        return Granted(entry);
                    }
                    if (Matches(entry.Owner, request)) return Granted(entry);
                    if (entry.Pending != null && !Matches(entry.Pending, request)) return Failure("another connection is already waiting for this player");
                    if (entry.Owner.Gateway.Length == 0)
                    {
                        Activate(entry, request);
                        return Granted(entry);
                    }
                    // Fast path for D7a: the owning gateway hasn't heartbeated inside GatewayStaleAfterSeconds, so
                    // nothing is ever going to release it. Take the claim over now — whether this is the first
                    // claim after the owner died, or a retry of an already-pending claim whose owner died mid-wait
                    // — rather than parking or re-parking it for the 15 s window below (which exists for an owner
                    // that might still be alive). This keeps the reclaim bound to roughly the staleness window
                    // plus one claim round trip, not the pending window's 15 s.
                    if ((entry.Pending == null || Matches(entry.Pending, request)) && _gatewayAlive != null && !_gatewayAlive(entry.Owner.Gateway))
                    {
                        RemoveRevocation(entry.Owner);
                        entry.Pending = null;
                        Activate(entry, request);
                        return Granted(entry);
                    }
                    entry.Pending = Copy(request);
                    entry.PendingUntil = now.AddSeconds(15);
                    if (!_revocations.TryGetValue(entry.Owner.Gateway, out var revoke))
                        _revocations[entry.Owner.Gateway] = revoke = new HashSet<string>(StringComparer.Ordinal);
                    revoke.Add(request.Identity);
                    return new GatewaySessionReply { Status = "pending" };
                }
                if (entry == null) return new GatewaySessionReply { Status = "gone" };
                if (request.Operation == "cancel" && entry.Pending != null && Matches(entry.Pending, request))
                {
                    RemoveRevocation(entry.Owner);
                    entry.Pending = null;
                    return new GatewaySessionReply { Status = "ok" };
                }
                if (!Matches(entry.Owner, request)) return new GatewaySessionReply { Status = "stale" };
                if (request.Operation == "release" || request.Operation == "cancel")
                {
                    if (request.Worker.Length > 0)
                    {
                        entry.Owner.Worker = request.Worker;
                        entry.Owner.Container = request.Container;
                    }
                    RemoveRevocation(entry.Owner);
                    if (entry.Pending != null)
                    {
                        Activate(entry, entry.Pending);
                        entry.Pending = null;
                    }
                    else
                    {
                        if (entry.Owner.Worker.Length == 0)
                        {
                            _players.Remove(request.Identity);
                            return new GatewaySessionReply { Status = "ok" };
                        }
                        entry.Owner.Gateway = "";
                        entry.Owner.Claim = "";
                        entry.RetainUntil = now.AddHours(24); // same lifetime as a session token
                        _retired.Enqueue(new KeyValuePair<string, DateTime>(request.Identity, entry.RetainUntil));
                    }
                    return new GatewaySessionReply { Status = "ok" };
                }
                if (request.Operation == "reserve")
                {
                    if (request.Worker.Length == 0) return Failure("spawn worker is required");
                    if (request.ObservedPawn || entry.Owner.Worker.Length == 0 || !_workerAlive(entry.Owner.Worker))
                    {
                        entry.Owner.Worker = request.Worker;
                        entry.Owner.Container = request.Container;
                    }
                    return Granted(entry);
                }
                return Failure("unknown session operation");
            }
        }

        private void RemoveRevocation(GatewaySessionRequest owner)
        {
            if (!_revocations.TryGetValue(owner.Gateway, out var pending)) return;
            pending.Remove(owner.Identity);
            if (pending.Count == 0) _revocations.Remove(owner.Gateway);
        }

        private void AddRevocation(Entry entry)
        {
            if (entry.Pending == null) return;
            if (!_revocations.TryGetValue(entry.Owner.Gateway, out var values))
                _revocations[entry.Owner.Gateway] = values = new HashSet<string>(StringComparer.Ordinal);
            values.Add(entry.Owner.Identity);
        }

        private static string Encode(Entry entry)
        {
            var w = new NetworkWriter();
            GatewaySessionWire.WriteRecord(w, entry.Owner);
            w.WriteBool(entry.Pending != null);
            if (entry.Pending != null) GatewaySessionWire.WriteRecord(w, entry.Pending);
            w.WriteULong((ulong)entry.PendingUntil.Ticks); w.WriteULong((ulong)entry.RetainUntil.Ticks);
            return Convert.ToBase64String(w.ToArray());
        }

        private static Entry Decode(string value)
        {
            var r = new NetworkReader(Convert.FromBase64String(value));
            var entry = new Entry { Owner = GatewaySessionWire.ReadRecord(r) };
            if (r.ReadBool()) entry.Pending = GatewaySessionWire.ReadRecord(r);
            entry.PendingUntil = new DateTime((long)r.ReadULong(), DateTimeKind.Utc);
            entry.RetainUntil = new DateTime((long)r.ReadULong(), DateTimeKind.Utc);
            return entry;
        }

        private static void Activate(Entry entry, GatewaySessionRequest request)
        {
            entry.Owner.Gateway = request.Gateway;
            entry.Owner.Claim = request.Claim;
            entry.Owner.Generation = checked(Math.Max(entry.Owner.Generation, request.Generation) + 1);
        }

        private static bool Matches(GatewaySessionRequest a, GatewaySessionRequest b) => a.Gateway == b.Gateway && a.Claim == b.Claim;
        private static GatewaySessionReply Granted(Entry entry) => new GatewaySessionReply { Status = "granted", Session = Copy(entry.Owner) };
        internal static GatewaySessionReply Failure(string error) => new GatewaySessionReply { Status = "error", Error = error };
        internal static GatewaySessionRequest Copy(GatewaySessionRequest r) => new GatewaySessionRequest
        {
            Operation = r.Operation,
            Identity = r.Identity,
            Gateway = r.Gateway,
            Claim = r.Claim,
            SessionId = r.SessionId,
            Generation = r.Generation,
            Worker = r.Worker,
            Container = r.Container,
            ObservedPawn = r.ObservedPawn
        };
    }

    // The compact envelope preserves 64-bit IDs exactly and reuses the existing bounded wire reader.
    internal static class GatewaySessionWire
    {
        internal static void WriteRecord(NetworkWriter w, GatewaySessionRequest r)
        {
            w.WriteString(r.Operation); w.WriteString(r.Identity); w.WriteString(r.Gateway); w.WriteString(r.Claim);
            w.WriteULong(r.SessionId); w.WriteULong(r.Generation); w.WriteString(r.Worker); r.Container.Write(w); w.WriteBool(r.ObservedPawn);
        }
        internal static GatewaySessionRequest ReadRecord(NetworkReader r) => new GatewaySessionRequest
        {
            Operation = r.ReadString(),
            Identity = r.ReadString(),
            Gateway = r.ReadString(),
            Claim = r.ReadString(),
            SessionId = r.ReadULong(),
            Generation = r.ReadULong(),
            Worker = r.ReadString(),
            Container = ContainerRef.Read(r),
            ObservedPawn = r.ReadBool()
        };
        private static string Envelope(NetworkWriter w) => "{\"data\":\"" + Convert.ToBase64String(w.ToArray()) + "\"}";
        private static NetworkReader Reader(string json)
        {
            if (json == null || json.Length > 1024 * 1024 || !PersistenceJson.TryParseObject(json, out var root, out _)) throw new FormatException("invalid session envelope");
            return new NetworkReader(Convert.FromBase64String(PersistenceJson.GetString(root, "data")));
        }
        internal static string Write(GatewaySessionRequest r) { var w = new NetworkWriter(); WriteRecord(w, r); return Envelope(w); }
        internal static GatewaySessionRequest ReadRequest(string json) => ReadRecord(Reader(json));
        internal static string Write(GatewaySessionReply reply)
        {
            var w = new NetworkWriter(); w.WriteString(reply.Status); w.WriteString(reply.Error); w.WriteBool(reply.Session != null);
            if (reply.Session != null) WriteRecord(w, reply.Session);
            w.WriteUShort((ushort)reply.Revocations.Count);
            foreach (var r in reply.Revocations) WriteRecord(w, r);
            return Envelope(w);
        }
        internal static GatewaySessionReply ReadReply(string json)
        {
            var r = Reader(json); var reply = new GatewaySessionReply { Status = r.ReadString(), Error = r.ReadString() };
            if (r.ReadBool()) reply.Session = ReadRecord(r);
            int count = r.ReadUShort(); if (count > 256) throw new FormatException("too many revocations");
            for (int i = 0; i < count; i++) reply.Revocations.Add(ReadRecord(r));
            return reply;
        }
    }

    sealed partial class LocalControlPlane : IGatewaySessionControlPlane
    {
        private GatewaySessionDirectory _sessionDirectory;
        /// <summary>
        /// Seconds since a gateway's last heartbeat before its claims become evictable (D7a / NEB-229). A test
        /// that wants to drive a hard-kill reclaim without waiting real time sets this alongside the fake
        /// <see cref="Clock"/>. The orchestrator sets this to <c>NebulaConfig.WorkerTimeoutSeconds</c>
        /// for both local and hosted control planes.
        /// </summary>
        internal double GatewayStaleAfterSeconds = GatewaySessionDirectory.DefaultGatewayStaleAfterSeconds;
        internal GatewaySessionDirectory SessionDirectory => _sessionDirectory ?? (_sessionDirectory = new GatewaySessionDirectory(
            id => { var worker = this.FindWorker(id); return worker != null && worker.Status != WorkerStatus.Dead; },
            gatewayAlive: GatewayKeyAlive));

        /// <summary>True while the gateway named by a <see cref="PlayerSessions.GatewayKey"/> (id#incarnation) is still registered under that incarnation and has heartbeated inside <see cref="GatewayStaleAfterSeconds"/>.</summary>
        private bool GatewayKeyAlive(string gatewayKey)
        {
            int hash = gatewayKey.LastIndexOf('#');
            if (hash < 0) return true; // malformed key: fail open rather than evict on a guess
            string gatewayId = gatewayKey.Substring(0, hash);
            if (!uint.TryParse(gatewayKey.Substring(hash + 1), out var incarnation)) return true;
            var g = this.FindGateway(gatewayId);
            if (g == null) return false; // never registered, or already unregistered: gone
            if (g.Incarnation != incarnation) return false; // a different process now owns this id: the old claim's owner is gone
            return (Now - g.LastHeartbeat).TotalSeconds <= GatewayStaleAfterSeconds;
        }

        void IGatewaySessionControlPlane.SessionRequest(GatewaySessionRequest request, Action<GatewaySessionReply> complete) => complete(SessionDirectory.Handle(request));
    }

    sealed partial class ControlPlaneHost : IGatewaySessionControlPlane
    {
        void IGatewaySessionControlPlane.SessionRequest(GatewaySessionRequest request, Action<GatewaySessionReply> complete) => complete(_plane.SessionDirectory.Handle(request));
    }

    sealed partial class RemoteControlPlane : IGatewaySessionControlPlane
    {
        private readonly ConcurrentQueue<Action> _sessionCallbacks = new ConcurrentQueue<Action>();
        private readonly SemaphoreSlim _sessionRequests = new SemaphoreSlim(8);
        private int _queuedSessionRequests;
        void IGatewaySessionControlPlane.SessionRequest(GatewaySessionRequest request, Action<GatewaySessionReply> complete)
        {
            if (Interlocked.Increment(ref _queuedSessionRequests) > 256)
            {
                Interlocked.Decrement(ref _queuedSessionRequests);
                complete(GatewaySessionDirectory.Failure("session coordinator is busy; try again later"));
                return;
            }
            string body = GatewaySessionWire.Write(request);
            Task.Run(async () =>
            {
                GatewaySessionReply reply;
                await _sessionRequests.WaitAsync();
                try
                {
                    using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    using (var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + GatewaySessionDirectory.Path))
                    {
                        if (_token != null) req.Headers.TryAddWithoutValidation(ControlPlaneHost.TokenHeader, _token);
                        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                        using (var response = await _http.SendAsync(req, cancel.Token))
                        {
                            response.EnsureSuccessStatusCode();
                            reply = GatewaySessionWire.ReadReply(await response.Content.ReadAsStringAsync());
                        }
                    }
                }
                catch (Exception) { reply = GatewaySessionDirectory.Failure("session coordinator is unavailable"); }
                finally { _sessionRequests.Release(); }
                if (_running) _sessionCallbacks.Enqueue(() =>
                {
                    Interlocked.Decrement(ref _queuedSessionRequests);
                    complete(reply);
                });
                else Interlocked.Decrement(ref _queuedSessionRequests);
            });
        }
    }
}
