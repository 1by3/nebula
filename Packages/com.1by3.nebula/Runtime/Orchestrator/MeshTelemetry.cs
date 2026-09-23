using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Nebula.World;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// The data behind the World map page of the Nebula Dashboard (<c>/map</c>). It has two halves:
    /// <list type="bullet">
    /// <item><b>Geometry</b>, built once on the orchestrator's main thread from <see cref="ContainerRegistry"/> and the
    /// world definition: every static container's box in absolute world coordinates, its cell, the container that
    /// encloses it and its neighbors (<c>GET /api/map/geometry</c>).</item>
    /// <item><b>Telemetry</b>, the latest document each worker posted (<see cref="WorkerTelemetry"/>,
    /// <c>POST /api/telemetry</c>): entity counts per container, the pose of every carried container whose carrier it
    /// simulates, the cells it has loaded and, while somebody has the map open, its authoritative entities
    /// (<c>GET /api/map</c>).</item>
    /// </list>
    /// Thread-safe: workers post, and the page reads, on the dashboard's listener threads. Worker documents are kept
    /// as received and spliced into the map document unparsed (the runtime carries no JSON reader); the page does the
    /// aggregation.
    /// </summary>
    public sealed class MeshTelemetry
    {
        /// <summary>Largest worker document accepted, in characters.</summary>
        public const int MaxDocumentChars = 4 * 1024 * 1024;
        /// <summary>Workers are asked for their entities while the map was read within this many seconds.</summary>
        public const double DetailWindowSeconds = 5.0;
        /// <summary>A worker document older than this is dropped: its worker died, retired or stopped posting.</summary>
        public const double ExpireSeconds = 15.0;

        private sealed class Document
        {
            public string Json;
            public double ReceivedAt;
        }

        private static readonly Regex WorkerIdPattern = new Regex("^\\s*\\{\\s*\"worker\"\\s*:\\s*\"([A-Za-z0-9_.:-]{1,64})\"", RegexOptions.CultureInvariant);
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly object _lock = new object();
        private readonly SortedDictionary<string, Document> _documents = new SortedDictionary<string, Document>(StringComparer.Ordinal);
        /// <summary>The latest per-container counts, by container id, from whichever worker reported each container last.</summary>
        private readonly Dictionary<string, ContainerLoad> _occupancy = new Dictionary<string, ContainerLoad>(StringComparer.Ordinal);
        /// <summary>The latest interest summary per worker, from the same documents.</summary>
        private readonly Dictionary<string, WorkerInterest> _interest = new Dictionary<string, WorkerInterest>(StringComparer.Ordinal);
        /// <summary>Container id -> when its hold expires on this process's clock, and which worker asked for it.</summary>
        private readonly Dictionary<string, Hold> _holds = new Dictionary<string, Hold>(StringComparer.Ordinal);
        /// <summary>Worker id -> the cohesion groups it last reported owning members of.</summary>
        private readonly Dictionary<string, List<CohesionSpan>> _cohesion = new Dictionary<string, List<CohesionSpan>>(StringComparer.Ordinal);
        /// <summary>
        /// The latest cost row per container (docs/cost-telemetry.md): what it costs the worker that leases it, in
        /// simulation, replication and gateway relay. Kept per container - which is per lease, a container has one
        /// owner - so the rows can be served as they are, and grouped by <see cref="ContainerCost.ScopeKey"/> by
        /// anything that wants a per-scope signal.
        /// </summary>
        private readonly Dictionary<string, ContainerCost> _cost = new Dictionary<string, ContainerCost>(StringComparer.Ordinal);
        private readonly List<KeyValuePair<string, ContainerLoad>> _parsed = new List<KeyValuePair<string, ContainerLoad>>();
        private readonly List<KeyValuePair<string, float>> _parsedHolds = new List<KeyValuePair<string, float>>();
        private readonly List<CohesionSpan> _parsedCohesion = new List<CohesionSpan>();
        private readonly List<ContainerCost> _parsedCost = new List<ContainerCost>();
        private readonly List<string> _expired = new List<string>();
        private readonly StringBuilder _sb = new StringBuilder(1 << 16);
        private readonly Func<double> _now;
        private string _geometry = "{\"partitioned\":false,\"containers\":[]}";
        private int _geometryVersion;
        private double _lastMapRead = double.NegativeInfinity;

        /// <param name="now">Seconds on a monotonic clock; a stopwatch by default.</param>
        public MeshTelemetry(Func<double> now = null)
        {
            _now = now ?? (() => Clock.Elapsed.TotalSeconds);
        }

        /// <summary>
        /// The tick budget a container's measured simulation time is weighed against when its dominant cost
        /// component is decided (<see cref="ContainerCost.Resolve"/>). The orchestrator sets it from the mesh's
        /// tick rate; it is <see cref="WorkerLoadTracker.TickPeriodMs"/> by default.
        /// </summary>
        public float TickPeriodMs = WorkerLoadTracker.TickPeriodMs;

        /// <summary>
        /// The outbound budget a container's bytes are weighed against, in bytes per second
        /// (<see cref="NebulaConfig.CostLinkBudgetMbps"/>). Only the comparison between the three components
        /// depends on it; the reported bytes are measured either way.
        /// </summary>
        public double LinkBytesPerSec = NebulaConfig.DefaultCostLinkBytesPerSec;

        /// <summary>
        /// How saturated a container has to be before it counts as at capacity
        /// (<see cref="NebulaConfig.CapacitySaturation"/>, docs/capacity-admission.md). Only reported here; the
        /// orchestrator is what publishes the flag to the mesh. 0 leaves every row below capacity.
        /// </summary>
        public float CapacitySaturation;

        /// <summary>Somebody read the map within <see cref="DetailWindowSeconds"/>, so workers should include their entities.</summary>
        public bool DetailWanted
        {
            get { lock (_lock) return _now() - _lastMapRead <= DetailWindowSeconds; }
        }

        /// <summary>Bumped by every <see cref="PublishGeometry"/>; the page refetches the geometry when it changes.</summary>
        public int GeometryVersion
        {
            get { lock (_lock) return _geometryVersion; }
        }

        /// <summary>The static geometry document served at <c>/api/map/geometry</c>.</summary>
        public string GeometryJson
        {
            get { lock (_lock) return _geometry; }
        }

        /// <summary>Workers with a document on file (expired ones are only dropped when the map is read).</summary>
        public int WorkerCount
        {
            get { lock (_lock) return _documents.Count; }
        }

        /// <summary>Replace the static geometry document (see <see cref="BuildGeometryJson"/>).</summary>
        public void PublishGeometry(string json)
        {
            lock (_lock)
            {
                _geometry = string.IsNullOrEmpty(json) ? "{\"partitioned\":false,\"containers\":[]}" : json;
                _geometryVersion++;
            }
        }

        /// <summary>
        /// Store a worker's telemetry document, replacing its previous one. The document must be a JSON object whose
        /// first property is <c>"worker"</c>, as <see cref="WorkerTelemetry"/> writes it. Returns null when it was
        /// stored, otherwise why it was refused. <paramref name="detail"/> is the answer for the worker: whether its
        /// next document should include entities.
        /// </summary>
        public string Accept(string json, out bool detail)
        {
            detail = DetailWanted;
            if (string.IsNullOrEmpty(json)) return "empty telemetry document";
            if (json.Length > MaxDocumentChars) return $"telemetry document larger than {MaxDocumentChars} characters";
            var m = WorkerIdPattern.Match(json);
            if (!m.Success) return "telemetry must be a JSON object whose first property is \"worker\"";
            int end = json.Length - 1;
            while (end > 0 && char.IsWhiteSpace(json[end])) end--;
            if (json[end] != '}') return "telemetry document is not a JSON object";
            string workerId = m.Groups[1].Value;
            lock (_lock)
            {
                double now = _now();
                _documents[workerId] = new Document { Json = json, ReceivedAt = now };
                _parsed.Clear();
                _parsedCost.Clear();
                ParseContainers(json, _parsed, _parsedCost);
                _expired.Clear();
                foreach (var kv in _occupancy) if (kv.Value.WorkerId == workerId) _expired.Add(kv.Key);
                foreach (var id in _expired) _occupancy.Remove(id);
                for (int i = 0; i < _parsed.Count; i++)
                {
                    var load = _parsed[i].Value;
                    load.WorkerId = workerId;
                    load.ReceivedAt = now;
                    _occupancy[_parsed[i].Key] = load;
                }
                AcceptCohesion(workerId, json, now);
                // Cost rows follow the same rule: this worker's previous rows go, and the document's replace them.
                // TickShare and the dominant component are relative to the document they came in, so they are
                // resolved here, over one worker's rows, and never across the mesh.
                _expired.Clear();
                foreach (var kv in _cost) if (kv.Value.WorkerId == workerId) _expired.Add(kv.Key);
                foreach (var id in _expired) _cost.Remove(id);
                ContainerCost.Normalize(_parsedCost, TickPeriodMs, LinkBytesPerSec);
                for (int i = 0; i < _parsedCost.Count; i++)
                {
                    var row = _parsedCost[i];
                    row.WorkerId = workerId;
                    row.ReceivedAt = now;
                    _cost[row.ContainerId] = row;
                }
                if (ParseInterest(json, out var interest))
                {
                    interest.ReceivedAt = now;
                    _interest[workerId] = interest;
                }
                else _interest.Remove(workerId);
            }
            return null;
        }

        /// <summary>
        /// Snapshot the latest per-container counts into <paramref name="result"/> (cleared first), dropping reports
        /// older than <see cref="ExpireSeconds"/>. Containers nobody reported are absent. This is what the
        /// cost-aware assignment policy reads once per pass.
        /// </summary>
        public void CopyOccupancy(Dictionary<string, ContainerLoad> result)
        {
            result.Clear();
            lock (_lock)
            {
                double now = _now();
                foreach (var kv in _occupancy) if (now - kv.Value.ReceivedAt <= ExpireSeconds) result[kv.Key] = kv.Value;
            }
        }

        /// <summary>
        /// Snapshot the latest cost row of every container into <paramref name="result"/> (cleared first), dropping
        /// reports older than <see cref="ExpireSeconds"/>. One row per container, which is one row per lease: this
        /// is the typed per-container signal the scaler explains a blocked grow with and <c>GET /api/cost</c>
        /// serves (docs/cost-telemetry.md).
        /// </summary>
        public void CopyContainerCost(Dictionary<string, ContainerCost> result)
        {
            result.Clear();
            lock (_lock)
            {
                double now = _now();
                foreach (var kv in _cost) if (now - kv.Value.ReceivedAt <= ExpireSeconds) result[kv.Key] = kv.Value;
            }
        }

        /// <summary>The cost rows as one JSON document, newest first by cost: the body of <c>GET /api/cost</c>.</summary>
        public string BuildCostJson()
        {
            var rows = new List<ContainerCost>();
            lock (_lock)
            {
                double now = _now();
                foreach (var kv in _cost) if (now - kv.Value.ReceivedAt <= ExpireSeconds) rows.Add(kv.Value);
            }
            rows.Sort((a, b) => b.TickShareMs != a.TickShareMs ? b.TickShareMs.CompareTo(a.TickShareMs) : string.CompareOrdinal(a.ContainerId, b.ContainerId));
            var sb = new StringBuilder(1 << 12);
            var w = new JsonWriter(sb);
            w.BeginObject();
            w.Prop("tickPeriodMs", TickPeriodMs);
            w.Prop("linkBytesPerSec", LinkBytesPerSec);
            w.Prop("capacitySaturation", CapacitySaturation);
            w.Key("containers");
            w.BeginArray();
            for (int i = 0; i < rows.Count; i++) ContainerCost.Write(w, rows[i], CapacitySaturation);
            w.EndArray();
            w.EndObject();
            return sb.ToString();
        }

        /// <summary>
        /// Pull the per-container counts out of a worker document without parsing the rest of it (the entity list
        /// can be megabytes). The array looks like <c>"containers":[{"id":"arena","players":1,"bots":0,...},...]</c>
        /// as <see cref="WorkerTelemetry"/> writes it; the slot for entities in no container ("") is skipped.
        /// <para>
        /// When <paramref name="costs"/> is given, the same pass also collects the cost row of each container
        /// (<see cref="ContainerCost"/>, docs/cost-telemetry.md). A document from a worker that does not write
        /// those keys yields rows of zeroes rather than nothing, which is what <see cref="ContainerLoad.HasEntityCost"/>
        /// is for: the counts still balance the mesh as they always did.
        /// </para>
        /// </summary>
        public static void ParseContainers(string json, List<KeyValuePair<string, ContainerLoad>> result, List<ContainerCost> costs = null)
        {
            int at = json.IndexOf("\"containers\"", StringComparison.Ordinal);
            if (at < 0) return;
            at = json.IndexOf('[', at);
            if (at < 0) return;
            at++;
            while (at < json.Length)
            {
                while (at < json.Length && (char.IsWhiteSpace(json[at]) || json[at] == ',')) at++;
                if (at >= json.Length || json[at] != '{') return;
                at++;
                string id = null;
                bool owned = true; // Older workers did not include an ownership marker.
                var load = new ContainerLoad();
                var cost = new ContainerCost { ScopeKey = "" };
                while (at < json.Length)
                {
                    while (at < json.Length && (char.IsWhiteSpace(json[at]) || json[at] == ',')) at++;
                    if (at >= json.Length) return;
                    if (json[at] == '}') { at++; break; }
                    if (json[at] != '"') return;
                    int keyEnd = json.IndexOf('"', at + 1);
                    if (keyEnd < 0) return;
                    string key = json.Substring(at + 1, keyEnd - at - 1);
                    at = json.IndexOf(':', keyEnd);
                    if (at < 0) return;
                    at++;
                    while (at < json.Length && char.IsWhiteSpace(json[at])) at++;
                    if (at >= json.Length) return;
                    if (json[at] == '"')
                    {
                        int strEnd = at + 1;
                        while (strEnd < json.Length && json[strEnd] != '"') { if (json[strEnd] == '\\') strEnd++; strEnd++; }
                        if (strEnd >= json.Length) return;
                        if (key == "id") id = json.Substring(at + 1, strEnd - at - 1);
                        else if (key == "scope") cost.ScopeKey = json.Substring(at + 1, strEnd - at - 1);
                        else if (key == "enclosing") load.Enclosing = json.Substring(at + 1, strEnd - at - 1);
                        at = strEnd + 1;
                    }
                    else
                    {
                        int numEnd = at;
                        while (numEnd < json.Length && (char.IsDigit(json[numEnd]) || json[numEnd] == '-' || json[numEnd] == '.' || json[numEnd] == 'e' || json[numEnd] == 'E' || json[numEnd] == '+')) numEnd++;
                        if (numEnd == at) return;
                        double.TryParse(json.Substring(at, numEnd - at), NumberStyles.Float, CultureInfo.InvariantCulture, out double number);
                        int value = (int)number;
                        switch (key)
                        {
                            case "owned": owned = value != 0; break;
                            case "players": load.Players = value; break;
                            case "bots": load.Bots = value; break;
                            case "serverDriven": load.ServerDriven = value; break;
                            case "other": load.Other = value; break;
                            case "ghosts": load.Ghosts = value; cost.GhostCount = value; break;
                            case "cost": load.EntityCostSum = (float)number; load.HasEntityCost = true; cost.EntityCostSum = (float)number; break;
                            case "tickMs": cost.TickShareMs = (float)number; break;
                            case "bytesOut": cost.BytesOutPerSec = (long)number; break;
                            case "gatewayBytes": cost.GatewayBytesPerSec = (long)number; break;
                        }
                        at = numEnd;
                    }
                }
                if (!string.IsNullOrEmpty(id) && owned)
                {
                    result?.Add(new KeyValuePair<string, ContainerLoad>(id, load));
                    if (costs != null) { cost.ContainerId = id; costs.Add(cost); }
                }
                while (at < json.Length && char.IsWhiteSpace(json[at])) at++;
                if (at < json.Length && json[at] == ']') return;
            }
        }

        /// <summary>
        /// The interest summary of one worker's latest document: how much of what it holds it actually sends,
        /// what that costs, and whether it is asking for the world to be partitioned.
        /// </summary>
        public struct WorkerInterest
        {
            public int Regions;
            public int Gateways;
            public double FilterMs;
            public bool Global;
            public long EntriesSent, EntriesTotal, BytesSent, BytesUnfiltered;
            public string Warning;
            public double ReceivedAt;
        }

        /// <summary>
        /// Snapshot each worker's interest summary into <paramref name="result"/> (cleared first), dropping reports
        /// older than <see cref="ExpireSeconds"/>. This is what the dashboard's worker rows read.
        /// </summary>
        public void CopyInterest(Dictionary<string, WorkerInterest> result)
        {
            result.Clear();
            lock (_lock)
            {
                double now = _now();
                foreach (var kv in _interest) if (now - kv.Value.ReceivedAt <= ExpireSeconds) result[kv.Key] = kv.Value;
            }
        }

        /// <summary>
        /// Pull the <c>"interest"</c> object out of a worker document without parsing the rest of it. Deliberately
        /// narrow: it reads the flat numeric and string properties of that one object and stops at its nested
        /// <c>"gateways"</c> array, which only the map page needs.
        /// </summary>
        public static bool ParseInterest(string json, out WorkerInterest interest)
        {
            interest = new WorkerInterest { Warning = "" };
            int at = json.IndexOf("\"interest\"", StringComparison.Ordinal);
            if (at < 0) return false;
            at = json.IndexOf('{', at);
            if (at < 0) return false;
            at++;
            while (at < json.Length)
            {
                while (at < json.Length && (char.IsWhiteSpace(json[at]) || json[at] == ',')) at++;
                if (at >= json.Length || json[at] == '}') return true;
                if (json[at] != '"') return true;
                int keyEnd = json.IndexOf('"', at + 1);
                if (keyEnd < 0) return true;
                string key = json.Substring(at + 1, keyEnd - at - 1);
                at = json.IndexOf(':', keyEnd);
                if (at < 0) return true;
                at++;
                while (at < json.Length && char.IsWhiteSpace(json[at])) at++;
                if (at >= json.Length) return true;
                char c = json[at];
                if (c == '[')
                {
                    // The per-gateway rows: counted here, read in full only by the map page.
                    int depth = 0;
                    int count = 0;
                    for (; at < json.Length; at++)
                    {
                        if (json[at] == '[') depth++;
                        else if (json[at] == ']') { depth--; if (depth == 0) { at++; break; } }
                        else if (json[at] == '{' && depth == 1) count++;
                    }
                    if (key == "gateways") interest.Gateways = count;
                }
                else if (c == '"')
                {
                    int strEnd = at + 1;
                    while (strEnd < json.Length && json[strEnd] != '"') { if (json[strEnd] == '\\') strEnd++; strEnd++; }
                    if (strEnd >= json.Length) return true;
                    if (key == "warning") interest.Warning = json.Substring(at + 1, strEnd - at - 1);
                    at = strEnd + 1;
                }
                else if (c == 't' || c == 'f')
                {
                    bool value = c == 't';
                    while (at < json.Length && char.IsLetter(json[at])) at++;
                    if (key == "global") interest.Global = value;
                }
                else
                {
                    int numEnd = at;
                    while (numEnd < json.Length && (char.IsDigit(json[numEnd]) || json[numEnd] == '-' || json[numEnd] == '.' || json[numEnd] == 'e' || json[numEnd] == 'E' || json[numEnd] == '+')) numEnd++;
                    if (numEnd == at) return true;
                    double.TryParse(json.Substring(at, numEnd - at), NumberStyles.Float, CultureInfo.InvariantCulture, out double value);
                    switch (key)
                    {
                        case "regions": interest.Regions = (int)value; break;
                        case "filterMs": interest.FilterMs = value; break;
                        case "entriesSent": interest.EntriesSent = (long)value; break;
                        case "entriesTotal": interest.EntriesTotal = (long)value; break;
                        case "bytesSent": interest.BytesSent = (long)value; break;
                        case "bytesUnfiltered": interest.BytesUnfiltered = (long)value; break;
                    }
                    at = numEnd;
                }
            }
            return true;
        }

        /// <summary>Drop a worker's document now instead of waiting for it to expire.</summary>
        public void Forget(string workerId)
        {
            if (workerId == null) return;
            lock (_lock)
            {
                _documents.Remove(workerId);
                _interest.Remove(workerId);
                _cohesion.Remove(workerId);
                _expired.Clear();
                foreach (var kv in _holds) if (kv.Value.WorkerId == workerId) _expired.Add(kv.Key);
                foreach (var id in _expired) _holds.Remove(id);
                _expired.Clear();
                foreach (var kv in _occupancy) if (kv.Value.WorkerId == workerId) _expired.Add(kv.Key);
                foreach (var id in _expired) _occupancy.Remove(id);
                _expired.Clear();
                foreach (var kv in _cost) if (kv.Value.WorkerId == workerId) _expired.Add(kv.Key);
                foreach (var id in _expired) _cost.Remove(id);
            }
        }

        /// <summary>
        /// The live map document: every worker's latest telemetry, with its age in seconds, sorted by worker id.
        /// Reading it marks the map as watched, which asks workers for their entities (<see cref="DetailWanted"/>).
        /// </summary>
        public string BuildMapJson()
        {
            lock (_lock)
            {
                double now = _now();
                _lastMapRead = now;
                _expired.Clear();
                foreach (var kv in _documents) if (now - kv.Value.ReceivedAt > ExpireSeconds) _expired.Add(kv.Key);
                foreach (var id in _expired) _documents.Remove(id);

                _sb.Clear();
                var w = new JsonWriter(_sb);
                w.BeginObject();
                w.Prop("serverTimeUtc", DateTime.UtcNow.ToString("o"));
                w.Prop("geometryVersion", _geometryVersion);
                w.Key("workers");
                w.BeginArray();
                foreach (var kv in _documents)
                {
                    w.BeginObject();
                    w.Prop("id", kv.Key);
                    w.Prop("ageSeconds", now - kv.Value.ReceivedAt);
                    w.Key("doc");
                    w.Raw(kv.Value.Json);
                    w.EndObject();
                }
                w.EndArray();
                w.EndObject();
                return _sb.ToString();
            }
        }

        // ---------------------------------------------------------------------------------------- cohesion hints

        /// <summary>One container a worker asked not to be rebalanced, as the orchestrator keeps it.</summary>
        private struct Hold
        {
            public string WorkerId;
            /// <summary>When the hold expires, on this object's clock.</summary>
            public double ExpiresAt;
        }

        /// <summary>What one worker said about one cohesion group in its latest document.</summary>
        public struct CohesionSpan
        {
            public uint Group;
            public int Members;
            public List<string> Containers;
            public string WorkerId;
            public double ReceivedAt;
        }

        /// <summary>
        /// Replace what <paramref name="workerId"/> last said about holds and cohesion groups. A hold is reported as
        /// the seconds it still has to run and becomes a deadline on the orchestrator's clock here, so the two
        /// processes need no common time base; the cost of that is one telemetry hop of latency
        /// (<c>docs/cohesion-hints.md</c>, D8). A worker that stops reporting loses its holds when its rows expire.
        /// Called with the lock held.
        /// </summary>
        private void AcceptCohesion(string workerId, string json, double now)
        {
            _expired.Clear();
            foreach (var kv in _holds) if (kv.Value.WorkerId == workerId) _expired.Add(kv.Key);
            foreach (var id in _expired) _holds.Remove(id);
            _parsedHolds.Clear();
            ParseHolds(json, _parsedHolds);
            for (int i = 0; i < _parsedHolds.Count; i++)
            {
                if (_parsedHolds[i].Value <= 0f) continue;
                _holds[_parsedHolds[i].Key] = new Hold { WorkerId = workerId, ExpiresAt = now + _parsedHolds[i].Value };
            }

            _parsedCohesion.Clear();
            ParseCohesion(json, _parsedCohesion);
            if (_parsedCohesion.Count == 0) { _cohesion.Remove(workerId); return; }
            var rows = new List<CohesionSpan>(_parsedCohesion.Count);
            for (int i = 0; i < _parsedCohesion.Count; i++)
            {
                var span = _parsedCohesion[i];
                span.WorkerId = workerId;
                span.ReceivedAt = now;
                rows.Add(span);
            }
            _cohesion[workerId] = rows;
        }

        /// <summary>
        /// Snapshot the live holds into <paramref name="result"/> (cleared first) as container id -> seconds still
        /// to run, dropping the ones that have expired. This is what the planner skips moves for.
        /// </summary>
        public void CopyHolds(Dictionary<string, float> result)
        {
            result.Clear();
            lock (_lock)
            {
                double now = _now();
                _expired.Clear();
                foreach (var kv in _holds)
                {
                    double left = kv.Value.ExpiresAt - now;
                    if (left <= 0.0) { _expired.Add(kv.Key); continue; }
                    result[kv.Key] = (float)left;
                }
                foreach (var id in _expired) _holds.Remove(id);
            }
        }

        /// <summary>The worker that asked for a container's hold, or "" when it is not held.</summary>
        public string HolderOf(string containerId)
        {
            if (containerId == null) return "";
            lock (_lock)
            {
                if (!_holds.TryGetValue(containerId, out var hold)) return "";
                return hold.ExpiresAt - _now() > 0.0 ? hold.WorkerId : "";
            }
        }

        /// <summary>
        /// Snapshot the cohesion groups every live worker reported into <paramref name="result"/> (cleared first),
        /// one row per group with the union of the containers its members sit in and the workers that reported it.
        /// Rows from a worker that stopped reporting are dropped after <see cref="ExpireSeconds"/>.
        /// </summary>
        public void CopyCohesion(List<CohesionGroupInfo> result)
        {
            result.Clear();
            lock (_lock)
            {
                double now = _now();
                foreach (var worker in _cohesion)
                {
                    var rows = worker.Value;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var span = rows[i];
                        if (now - span.ReceivedAt > ExpireSeconds) continue;
                        CohesionGroupInfo info = null;
                        for (int j = 0; j < result.Count; j++) if (result[j].Group == span.Group) { info = result[j]; break; }
                        if (info == null) result.Add(info = new CohesionGroupInfo { Group = span.Group });
                        info.Members += span.Members;
                        if (!info.Workers.Contains(span.WorkerId)) info.Workers.Add(span.WorkerId);
                        if (span.Containers != null)
                            for (int c = 0; c < span.Containers.Count; c++)
                                if (!info.Containers.Contains(span.Containers[c])) info.Containers.Add(span.Containers[c]);
                    }
                }
            }
            result.Sort((a, b) => a.Group.CompareTo(b.Group));
        }

        /// <summary>
        /// Pull the <c>"holds"</c> array out of a worker document: <c>[{"id":"arena","seconds":4.2},...]</c>, as
        /// <see cref="WorkerTelemetry"/> writes it before the container counts and the entity list.
        /// </summary>
        public static void ParseHolds(string json, List<KeyValuePair<string, float>> result)
        {
            int at = ArrayStart(json, "\"holds\"");
            while (at >= 0)
            {
                at = ObjectStart(json, at, out bool done);
                if (done) return;
                string id = null;
                float seconds = 0f;
                while (NextProperty(json, ref at, out string key, out string text, out double number, out bool isString))
                {
                    if (key == "id" && isString) id = text;
                    else if (key == "seconds" && !isString) seconds = (float)number;
                }
                if (!string.IsNullOrEmpty(id)) result.Add(new KeyValuePair<string, float>(id, seconds));
            }
        }

        /// <summary>
        /// Pull the <c>"cohesion"</c> array out of a worker document:
        /// <c>[{"group":17,"members":3,"in":["a","b"]},...]</c>. The containers are under <c>"in"</c> rather than
        /// <c>"containers"</c> so that <see cref="ParseContainers"/>, which scans for the first <c>"containers"</c>
        /// key in the document, cannot land in this block. Rows without a group are ignored.
        /// </summary>
        public static void ParseCohesion(string json, List<CohesionSpan> result)
        {
            int at = ArrayStart(json, "\"cohesion\"");
            while (at >= 0)
            {
                at = ObjectStart(json, at, out bool done);
                if (done) return;
                var span = new CohesionSpan { Containers = new List<string>(2) };
                while (true)
                {
                    while (at < json.Length && (char.IsWhiteSpace(json[at]) || json[at] == ',')) at++;
                    if (at >= json.Length) return;
                    if (json[at] == '}') { at++; break; }
                    if (json[at] != '"') return;
                    int keyEnd = json.IndexOf('"', at + 1);
                    if (keyEnd < 0) return;
                    string key = json.Substring(at + 1, keyEnd - at - 1);
                    at = json.IndexOf(':', keyEnd);
                    if (at < 0) return;
                    at++;
                    while (at < json.Length && char.IsWhiteSpace(json[at])) at++;
                    if (at >= json.Length) return;
                    if (json[at] == '[')
                    {
                        at++;
                        while (true)
                        {
                            while (at < json.Length && (char.IsWhiteSpace(json[at]) || json[at] == ',')) at++;
                            if (at >= json.Length) return;
                            if (json[at] == ']') { at++; break; }
                            if (json[at] != '"') return;
                            int end = EndOfString(json, at);
                            if (end < 0) return;
                            string value = json.Substring(at + 1, end - at - 1);
                            if (key == "in" && value.Length > 0 && !span.Containers.Contains(value)) span.Containers.Add(value);
                            at = end + 1;
                        }
                    }
                    else if (json[at] == '"')
                    {
                        int end = EndOfString(json, at);
                        if (end < 0) return;
                        at = end + 1;
                    }
                    else
                    {
                        int numEnd = at;
                        while (numEnd < json.Length && (char.IsDigit(json[numEnd]) || json[numEnd] == '-' || json[numEnd] == '.' || json[numEnd] == 'e' || json[numEnd] == 'E' || json[numEnd] == '+')) numEnd++;
                        if (numEnd == at) return;
                        double.TryParse(json.Substring(at, numEnd - at), NumberStyles.Float, CultureInfo.InvariantCulture, out double value);
                        if (key == "group") span.Group = (uint)value;
                        else if (key == "members") span.Members = (int)value;
                        at = numEnd;
                    }
                }
                if (span.Group != 0) result.Add(span);
            }
        }

        /// <summary>Index just after the '[' of the named array, or -1 when the document has none.</summary>
        private static int ArrayStart(string json, string key)
        {
            int at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return -1;
            at = json.IndexOf('[', at);
            return at < 0 ? -1 : at + 1;
        }

        /// <summary>Index just after the next '{' of an array; <paramref name="done"/> at anything else (its ']').</summary>
        private static int ObjectStart(string json, int at, out bool done)
        {
            while (at < json.Length && (char.IsWhiteSpace(json[at]) || json[at] == ',')) at++;
            done = at >= json.Length || json[at] != '{';
            return done ? at : at + 1;
        }

        /// <summary>Index of the closing quote of the string starting at <paramref name="at"/>, or -1.</summary>
        private static int EndOfString(string json, int at)
        {
            int end = at + 1;
            while (end < json.Length && json[end] != '"') { if (json[end] == '\\') end++; end++; }
            return end < json.Length ? end : -1;
        }

        /// <summary>
        /// Read the next property of a flat object (no nested arrays or objects) and advance past it. False at the
        /// object's '}' or on anything malformed.
        /// </summary>
        private static bool NextProperty(string json, ref int at, out string key, out string text, out double number, out bool isString)
        {
            key = null; text = null; number = 0; isString = false;
            while (at < json.Length && (char.IsWhiteSpace(json[at]) || json[at] == ',')) at++;
            if (at >= json.Length || json[at] == '}') { if (at < json.Length) at++; return false; }
            if (json[at] != '"') return false;
            int keyEnd = json.IndexOf('"', at + 1);
            if (keyEnd < 0) return false;
            key = json.Substring(at + 1, keyEnd - at - 1);
            at = json.IndexOf(':', keyEnd);
            if (at < 0) return false;
            at++;
            while (at < json.Length && char.IsWhiteSpace(json[at])) at++;
            if (at >= json.Length) return false;
            if (json[at] == '"')
            {
                int end = EndOfString(json, at);
                if (end < 0) return false;
                text = json.Substring(at + 1, end - at - 1);
                isString = true;
                at = end + 1;
                return true;
            }
            int numEnd = at;
            while (numEnd < json.Length && (char.IsDigit(json[numEnd]) || json[numEnd] == '-' || json[numEnd] == '.' || json[numEnd] == 'e' || json[numEnd] == 'E' || json[numEnd] == '+')) numEnd++;
            if (numEnd == at) return false;
            double.TryParse(json.Substring(at, numEnd - at), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
            at = numEnd;
            return true;
        }

        // ---------------------------------------------------------------------------------------- geometry

        /// <summary>
        /// Every container of the loaded level as the map draws it: its box in absolute world coordinates
        /// (<see cref="ToAbsolute(Vector3, out double, out double, out double)"/>), its cell when it belongs to an
        /// authored partition, the smallest container enclosing it and its static neighbors, plus the world's cells
        /// and the ghost band and hysteresis the mesh runs with. A runtime-only world is partitioned even though its
        /// authored container grid is empty. Main thread.
        /// </summary>
        public static string BuildGeometryJson(NebulaConfig config)
        {
            var sb = new StringBuilder(4096);
            var w = new JsonWriter(sb);
            var world = WorldOrigin.Definition;
            bool partitioned = world != null;
            bool gridded = ContainerRegistry.IsGridded && partitioned;
            w.BeginObject();
            w.Prop("partitioned", partitioned);
            if (partitioned)
            {
                w.Key("world");
                w.BeginObject();
                w.Prop("name", world.WorldName);
                WriteVector(w, "cellSize", world.CellSize.x, world.CellSize.y, world.CellSize.z);
                w.Key("cells");
                w.BeginArray();
                foreach (var cell in world.Cells)
                {
                    w.BeginObject();
                    WriteCell(w, "coord", cell.Coord);
                    w.Prop("scene", cell.SceneName);
                    w.EndObject();
                }
                w.EndArray();
                w.EndObject();
            }
            w.Prop("ghostBandMargin", config != null ? config.GhostBandMargin : 0f);
            w.Prop("handoverHysteresis", config != null ? config.HandoverHysteresis : 0f);
            w.Key("containers");
            w.BeginArray();
            foreach (var c in ContainerRegistry.All) WriteContainer(w, c, gridded);
            foreach (var c in ContainerRegistry.Runtime) WriteContainer(w, c, gridded);
            w.EndArray();
            w.EndObject();
            return sb.ToString();
        }

        private static void WriteContainer(JsonWriter w, Container c, bool gridded)
        {
            w.BeginObject();
            w.Prop("id", c.ContainerId);
            w.Prop("index", (int)c.Index);
            if (c.IsRuntime) w.Prop("runtime", true);
            if (gridded && !c.IsRuntime)
            {
                WriteCell(w, "cell", c.Cell);
                w.Prop("isCell", c.IsCell);
            }
            WriteBox(w, c);
            var parent = EnclosingStatic(c);
            w.Prop("parent", parent != null ? parent.ContainerId : "");
            w.Key("neighbors");
            w.BeginArray();
            foreach (var n in c.Neighbors) w.Value(n.ContainerId);
            w.EndArray();
            w.EndObject();
        }

        /// <summary>The smallest static container whose box holds <paramref name="c"/>'s, or null. In a partitioned world only <paramref name="c"/>'s own cell is searched.</summary>
        public static Container EnclosingStatic(Container c)
        {
            if (c == null || c.IsDynamic || c.IsRuntime) return null;
            var candidates = ContainerRegistry.IsGridded ? ContainerRegistry.InCell(c.Cell) : ContainerRegistry.All;
            Container best = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                var o = candidates[i];
                if (o == c || !o.Encloses(c)) continue;
                if (best == null || o.Volume < best.Volume) best = o;
            }
            return best;
        }

        /// <summary>
        /// Absolute world position of a position in this process's frame. Without a partitioned world the frame is
        /// the world. With one, every process keeps its own floating origin (<see cref="WorldOrigin"/>), so the origin
        /// cell's offset is added back: two workers report the same place with the same numbers.
        /// </summary>
        public static void ToAbsolute(Vector3 frame, out double x, out double y, out double z)
        {
            var world = WorldOrigin.Definition;
            if (world == null)
            {
                x = frame.x; y = frame.y; z = frame.z;
                return;
            }
            ToAbsolute(frame, WorldOrigin.Cell, world.CellSize, out x, out y, out z);
        }

        /// <summary>Absolute world position of <paramref name="frame"/> in the frame whose origin cell is <paramref name="originCell"/>.</summary>
        public static void ToAbsolute(Vector3 frame, Vector3Int originCell, Vector3 cellSize, out double x, out double y, out double z)
        {
            x = (double)originCell.x * cellSize.x + frame.x;
            y = (double)originCell.y * cellSize.y + frame.y;
            z = (double)originCell.z * cellSize.z + frame.z;
        }

        /// <summary>A container's box: <c>center</c> (absolute), <c>size</c> (scaled) and <c>rotation</c> (quaternion x, y, z, w).</summary>
        internal static void WriteBox(JsonWriter w, Container c)
        {
            var center = c.ToWorld(c.Center);
            ToAbsolute(center, out double x, out double y, out double z);
            WriteVector(w, "center", x, y, z);
            var scale = c.transform.lossyScale;
            WriteVector(w, "size", Mathf.Abs(c.Size.x * scale.x), Mathf.Abs(c.Size.y * scale.y), Mathf.Abs(c.Size.z * scale.z));
            var r = c.Rotation;
            w.Key("rotation");
            w.BeginArray();
            w.Value((double)r.x);
            w.Value((double)r.y);
            w.Value((double)r.z);
            w.Value((double)r.w);
            w.EndArray();
        }

        internal static void WriteVector(JsonWriter w, string key, double x, double y, double z)
        {
            w.Key(key);
            w.BeginArray();
            w.Value(Math.Round(x, 2));
            w.Value(Math.Round(y, 2));
            w.Value(Math.Round(z, 2));
            w.EndArray();
        }

        internal static void WriteCell(JsonWriter w, string key, Vector3Int cell)
        {
            w.Key(key);
            w.BeginArray();
            w.Value((long)cell.x);
            w.Value((long)cell.y);
            w.Value((long)cell.z);
            w.EndArray();
        }
    }
}
