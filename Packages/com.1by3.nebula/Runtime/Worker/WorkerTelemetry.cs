using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Reports what a worker holds to the World map of the Nebula Dashboard (<see cref="MeshTelemetry"/>):
    /// <list type="bullet">
    /// <item>for every container with anything in it on this worker, how many players, bots, server-driven and other
    /// authoritative entities it holds there, and how many ghosts;</item>
    /// <item>the box, pose and velocity of every carried container (<see cref="DynamicContainer"/>) whose carrier this
    /// worker simulates, with the container the carrier sits in;</item>
    /// <item>the cells it has loaded, in a partitioned world;</item>
    /// <item>and, only while somebody has the map open, one point per authoritative entity.</item>
    /// </list>
    /// Positions are absolute world coordinates, so workers with different floating origins agree. The document is
    /// posted to <c>-nebula-telemetry &lt;url&gt;</c> (the orchestrator passes its own dashboard's
    /// <c>/api/telemetry</c> to every worker it launches), or handed straight to an orchestrator in the same process.
    /// It is built in <c>Update</c>, never inside the tick, every <see cref="IdleIntervalSeconds"/> or
    /// <see cref="DetailIntervalSeconds"/> while the map is watched, and sent from the thread pool; a post still in
    /// flight skips the next one rather than queueing.
    /// </summary>
    public sealed class WorkerTelemetry : IDisposable
    {
        /// <summary>Seconds between documents while nobody has the map open (counts and carried containers only).</summary>
        public const float IdleIntervalSeconds = 1f;
        /// <summary>Seconds between documents while the map is watched (with entities).</summary>
        public const float DetailIntervalSeconds = 0.5f;
        /// <summary>Most entity points in one document; the rest are counted but not drawn.</summary>
        public const int MaxEntities = 4096;

        /// <summary>Entity kinds as the map receives them.</summary>
        public const string KindPlayer = "p", KindBot = "b", KindServerDriven = "n", KindCarrier = "v", KindOther = "o";

        private struct Counts
        {
            public int Players, Bots, ServerDriven, Other, Ghosts;
        }

        /// <summary>
        /// What interest management cost and saved since the previous document. Rates rather than
        /// totals: "this worker sends 40% of its entries and 1.2 MB/s instead of 4 MB/s" is the question the
        /// dashboard answers, and a counter that only grows cannot answer it.
        /// </summary>
        private struct InterestSample
        {
            public long EntriesSent, EntriesTotal, BytesSent, BytesUnfiltered;
            public float At;
        }

        private InterestSample _interestPrevious;
        private InterestSample _interestRate;
        private bool _hasInterestRate;
        private int _interestRegions;
        private float _interestFilterMs;
        private bool _interestGlobal;
        private string _partitionWarning = "";
        private readonly List<NebulaWorker.GatewayInterest> _gatewayInterest = new List<NebulaWorker.GatewayInterest>();

        private readonly string _url;
        private readonly MeshTelemetry _inProcess;
        private readonly HttpClient _http;
        private readonly StringBuilder _sb = new StringBuilder(1 << 14);
        private readonly Dictionary<string, int> _slotById = new Dictionary<string, int>();
        private readonly List<string> _slotIds = new List<string>();
        private readonly List<Counts> _counts = new List<Counts>();
        private readonly List<NetworkIdentity> _carriers = new List<NetworkIdentity>();
        private readonly List<NetworkIdentity> _points = new List<NetworkIdentity>();
        private readonly List<NebulaWorker.ContainerHold> _holds = new List<NebulaWorker.ContainerHold>();
        private readonly List<NebulaWorker.CohesionSpan> _cohesion = new List<NebulaWorker.CohesionSpan>();
        private float _next;
        private int _inFlight;
        private volatile bool _detail;
        private volatile string _error;
        private float _nextErrorLog;

        /// <summary>Where documents are posted; empty when they go to an orchestrator in this process.</summary>
        public string Url => _url ?? "";
        /// <summary>The orchestrator asked for entities (somebody has the map open).</summary>
        public bool DetailRequested => _detail;
        /// <summary>Documents built and handed off so far.</summary>
        public int Sent { get; private set; }

        private WorkerTelemetry(string url, MeshTelemetry inProcess)
        {
            _url = url;
            _inProcess = inProcess;
            if (inProcess == null) _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        }

        /// <summary>
        /// The telemetry sender for a worker: straight into <paramref name="inProcess"/> when an orchestrator runs in
        /// the same process, else to the URL of <c>-nebula-telemetry</c>. Null when neither applies, or when the switch
        /// is <c>off</c>.
        /// </summary>
        public static WorkerTelemetry Create(MeshTelemetry inProcess)
        {
            string url = CommandLine.Get("nebula-telemetry", "");
            if (string.Equals(url, "off", StringComparison.OrdinalIgnoreCase)) return null;
            if (inProcess != null) return new WorkerTelemetry(null, inProcess);
            return string.IsNullOrEmpty(url) ? null : new WorkerTelemetry(url, null);
        }

        /// <summary>For tests: a sender that writes documents but has nowhere to send them.</summary>
        internal static WorkerTelemetry ForTests() => new WorkerTelemetry(null, new MeshTelemetry());

        /// <summary>Main thread, once a frame: build and send a document when one is due.</summary>
        internal void Update(NebulaWorker worker)
        {
            float now = Time.unscaledTime;
            string error = _error;
            if (error != null && now >= _nextErrorLog)
            {
                _error = null;
                _nextErrorLog = now + 30f;
                NebulaLog.Warn($"telemetry to {Url} failed: {error} (the World map will not show {worker.WorkerId})");
            }
            if (now < _next || Volatile.Read(ref _inFlight) != 0) return;
            _next = now + (_detail ? DetailIntervalSeconds : IdleIntervalSeconds);

            SampleInterest(worker, now);
            // The cohesion hints are read here, on the main thread, and written into the document below.
            worker.CopyHolds(_holds);
            worker.CopyCohesion(_cohesion);
            var streamer = NebulaWorld.IsActive ? NebulaWorld.Streamer : null;
            string json = Write(worker.WorkerId, worker.WorkerIndex, worker.CurrentTick, worker.Entities, _detail, streamer != null ? streamer.LoadedCells : null);
            Sent++;
            if (_inProcess != null)
            {
                string refused = _inProcess.Accept(json, out bool detail);
                _detail = detail;
                if (refused != null) _error = refused;
                return;
            }
            Interlocked.Exchange(ref _inFlight, 1);
            Task.Run(() => PostAsync(json));
        }

        /// <summary>Take the interest counters and turn them into per-second rates for this document. Main thread.</summary>
        private void SampleInterest(NebulaWorker worker, float now)
        {
            var current = new InterestSample
            {
                EntriesSent = worker.InterestEntriesSent,
                EntriesTotal = worker.InterestEntriesTotal,
                BytesSent = worker.InterestBytesSent,
                BytesUnfiltered = worker.InterestBytesUnfiltered,
                At = now,
            };
            float dt = now - _interestPrevious.At;
            if (_interestPrevious.At > 0f && dt > 0.001f)
            {
                _interestRate = new InterestSample
                {
                    EntriesSent = (long)((current.EntriesSent - _interestPrevious.EntriesSent) / dt),
                    EntriesTotal = (long)((current.EntriesTotal - _interestPrevious.EntriesTotal) / dt),
                    BytesSent = (long)((current.BytesSent - _interestPrevious.BytesSent) / dt),
                    BytesUnfiltered = (long)((current.BytesUnfiltered - _interestPrevious.BytesUnfiltered) / dt),
                    At = now,
                };
                _hasInterestRate = true;
            }
            _interestPrevious = current;
            _interestRegions = worker.SubscribedRegions;
            _interestFilterMs = worker.InterestFilterMs;
            _interestGlobal = worker.HasGlobalEntities;
            _partitionWarning = worker.PartitionWarning ?? "";
            worker.CopyGatewayInterest(_gatewayInterest);
        }

        /// <summary>The interest block of the document: totals, per-gateway rows, and the partition warning.</summary>
        private void WriteInterest(JsonWriter w)
        {
            w.Key("interest");
            w.BeginObject();
            w.Prop("regions", _interestRegions);
            w.Prop("filterMs", Math.Round(_interestFilterMs, 3));
            w.Prop("global", _interestGlobal);
            w.Prop("entriesSent", _hasInterestRate ? _interestRate.EntriesSent : 0L);
            w.Prop("entriesTotal", _hasInterestRate ? _interestRate.EntriesTotal : 0L);
            w.Prop("bytesSent", _hasInterestRate ? _interestRate.BytesSent : 0L);
            w.Prop("bytesUnfiltered", _hasInterestRate ? _interestRate.BytesUnfiltered : 0L);
            w.Prop("warning", _partitionWarning);
            w.Key("gateways");
            w.BeginArray();
            for (int i = 0; i < _gatewayInterest.Count; i++)
            {
                var g = _gatewayInterest[i];
                w.BeginObject();
                w.Prop("id", g.GatewayId ?? "");
                w.Prop("regions", g.Regions);
                w.Prop("foci", g.Foci);
                w.Prop("entities", g.ExplicitEntities);
                w.Prop("entriesSent", g.EntriesSent);
                w.Prop("bytesSent", g.BytesSent);
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
        }

        /// <summary>
        /// The cohesion block: the containers this worker is holding, with the seconds each hold still has to run,
        /// and one row per cohesion group it owns members of (<c>docs/cohesion-hints.md</c>, D7/D8). Both are
        /// always written, so a document that carries neither says so with two empty arrays.
        /// </summary>
        private void WriteCohesion(JsonWriter w)
        {
            w.Key("holds");
            w.BeginArray();
            for (int i = 0; i < _holds.Count; i++)
            {
                w.BeginObject();
                w.Prop("id", _holds[i].ContainerId ?? "");
                w.Prop("seconds", Math.Round(_holds[i].SecondsRemaining, 2));
                w.EndObject();
            }
            w.EndArray();

            w.Key("cohesion");
            w.BeginArray();
            for (int i = 0; i < _cohesion.Count; i++)
            {
                var span = _cohesion[i];
                w.BeginObject();
                w.Prop("group", (long)span.Group);
                w.Prop("members", span.Members);
                w.Key("containers");
                w.BeginArray();
                if (span.Containers != null) for (int j = 0; j < span.Containers.Count; j++) w.Value(span.Containers[j]);
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();
        }

        private async Task PostAsync(string json)
        {
            try
            {
                using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                using (var response = await _http.PostAsync(_url, content).ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        _detail = body.IndexOf("\"detail\":true", StringComparison.Ordinal) >= 0;
                    }
                    else
                    {
                        _detail = false;
                        _error = $"HTTP {(int)response.StatusCode} {body}";
                    }
                }
            }
            catch (Exception e)
            {
                _detail = false;
                _error = e.GetBaseException().Message;
            }
            finally
            {
                Interlocked.Exchange(ref _inFlight, 0);
            }
        }

        /// <summary>
        /// Write one telemetry document for the entities a worker holds (authoritative and ghosts). The first property
        /// is always <c>"worker"</c>: <see cref="MeshTelemetry.Accept"/> reads the id from there without parsing.
        /// </summary>
        public string Write(string workerId, ushort workerIndex, uint tick, IEnumerable<NetworkIdentity> entities, bool includeEntities, IEnumerable<Vector3Int> loadedCells)
        {
            _slotById.Clear();
            _slotIds.Clear();
            _counts.Clear();
            _carriers.Clear();
            _points.Clear();
            if (entities != null)
            {
                foreach (var e in entities)
                {
                    if (e == null) continue;
                    int slot = SlotOf(e.Container);
                    var c = _counts[slot];
                    if (!e.HasAuthority) c.Ghosts++;
                    else if (e.IsServerDriven) c.ServerDriven++;
                    else if (e.OwnerClientId == 0) c.Other++;
                    else if (e.OwnerIsBot) c.Bots++;
                    else c.Players++;
                    _counts[slot] = c;
                    if (!e.HasAuthority) continue;
                    if (e.Carried != null && e.Carried.IsDynamic) _carriers.Add(e);
                    if (includeEntities) _points.Add(e);
                }
            }

            _sb.Clear();
            var w = new JsonWriter(_sb);
            w.BeginObject();
            w.Prop("worker", workerId ?? "");
            w.Prop("index", (int)workerIndex);
            w.Prop("tick", tick);
            w.Prop("detail", includeEntities);
            WriteInterest(w);
            if (Nebula.World.WorldOrigin.Definition != null) MeshTelemetry.WriteCell(w, "origin", Nebula.World.WorldOrigin.Cell);
            if (loadedCells != null)
            {
                w.Key("loadedCells");
                w.BeginArray();
                foreach (var cell in loadedCells)
                {
                    w.BeginArray();
                    w.Value((long)cell.x);
                    w.Value((long)cell.y);
                    w.Value((long)cell.z);
                    w.EndArray();
                }
                w.EndArray();
            }

            WriteCohesion(w);

            w.Key("containers");
            w.BeginArray();
            for (int i = 0; i < _slotIds.Count; i++)
            {
                var c = _counts[i];
                w.BeginObject();
                w.Prop("id", _slotIds[i]);
                w.Prop("players", c.Players);
                w.Prop("bots", c.Bots);
                w.Prop("serverDriven", c.ServerDriven);
                w.Prop("other", c.Other);
                w.Prop("ghosts", c.Ghosts);
                w.EndObject();
            }
            w.EndArray();

            w.Key("carried");
            w.BeginArray();
            foreach (var carrier in _carriers)
            {
                var box = carrier.Carried;
                w.BeginObject();
                w.Prop("id", box.ContainerId);
                w.Prop("carrier", carrier.NetId.ToString(CultureInfo.InvariantCulture));
                w.Prop("enclosing", carrier.Container != null ? carrier.Container.ContainerId : "");
                w.Prop("depth", box.NestingDepth);
                w.Prop("pinned", box.IsPinned);
                w.Prop("contents", box.Entities.Count);
                MeshTelemetry.WriteBox(w, box);
                MeshTelemetry.ToAbsolute(carrier.transform.position, out double px, out double py, out double pz);
                MeshTelemetry.WriteVector(w, "position", px, py, pz);
                var v = carrier.Motion.Velocity;
                MeshTelemetry.WriteVector(w, "velocity", v.x, v.y, v.z);
                w.EndObject();
            }
            w.EndArray();

            if (includeEntities)
            {
                w.Key("entities");
                w.BeginArray();
                int written = 0;
                foreach (var e in _points)
                {
                    if (written == MaxEntities) break;
                    written++;
                    var t = e.transform;
                    MeshTelemetry.ToAbsolute(t.position, out double x, out double y, out double z);
                    string kind = KindOf(e);
                    // [net id, kind, x, y, z, yaw, container slot (index into "containers", -1 for none), name for players and bots]
                    w.BeginArray();
                    w.Value(e.NetId.ToString(CultureInfo.InvariantCulture));
                    w.Value(kind);
                    w.Value(Math.Round(x, 2));
                    w.Value(Math.Round(y, 2));
                    w.Value(Math.Round(z, 2));
                    w.Value((long)Mathf.RoundToInt(t.eulerAngles.y));
                    w.Value((long)(e.Container != null ? _slotById[e.Container.ContainerId] : -1));
                    if (kind == KindPlayer || kind == KindBot) w.Value(e.name);
                    w.EndArray();
                }
                w.EndArray();
                w.Prop("entitiesTruncated", _points.Count > MaxEntities);
            }
            w.EndObject();
            return _sb.ToString();
        }

        /// <summary>How the map draws an authoritative entity: a carrier, a server-driven entity, a bot's or a player's pawn, or anything else.</summary>
        public static string KindOf(NetworkIdentity e)
        {
            if (e.Carried != null) return KindCarrier;
            if (e.IsServerDriven) return KindServerDriven;
            if (e.OwnerClientId == 0) return KindOther;
            return e.OwnerIsBot ? KindBot : KindPlayer;
        }

        private int SlotOf(Container container)
        {
            string id = container != null ? container.ContainerId : "";
            if (_slotById.TryGetValue(id, out int slot)) return slot;
            slot = _slotIds.Count;
            _slotById[id] = slot;
            _slotIds.Add(id);
            _counts.Add(default);
            return slot;
        }

        public void Dispose()
        {
            _http?.Dispose();
        }
    }
}
